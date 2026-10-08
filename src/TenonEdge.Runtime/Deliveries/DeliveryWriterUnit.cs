using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 发送缓存的唯一写者（总设计 §8.2）：从请求队列取出请求，收到第一个请求后最多再等一个提交间隔，或凑满条数上限，
/// 把期间的请求放进同一个事务提交；提交返回后才逐个报告成功。
/// <list type="bullet">
/// <item>提交失败（例如 SQLITE_FULL）时整批显式回滚，逐个报告失败，记“存储异常”运行事件；写连接随后重新打开，
/// 下一批照常尝试，成功时记“已恢复”。不绕过持久化假装成功（总设计 §8.2、§8.5）。</item>
/// <item>每个请求在自己的保存点中执行：插入的约束错误（例如投递编号与已有记录重复）只回滚并拒绝该请求，记为数据错误，
/// 同批其他请求照常提交，不记“存储异常”。</item>
/// <item>停止：取消令牌之后不再等待提交间隔，把请求队列中已有的请求全部提交后返回（停机顺序中的“最后一次提交”）。
/// 运行时在输入停止之后才停止写者，此后请求队列不再接受新请求。</item>
/// <item>打不开发送缓存时（文件损坏、权限、SQLite 版本或持久化设置不符），记“存储异常”，请求队列中已有的请求报告失败，然后异常退出，
/// 由监督者按退避重启后再试；之后到达的请求等待新实例处理。写者本身因其他原因异常退出时，已取出、尚未完成的请求报告失败。</item>
/// </list>
/// </summary>
internal sealed partial class DeliveryWriterUnit(
    DeliveryCache queue,
    TimeProvider time,
    IRuntimeEventSink events,
    ILogger logger) : IRuntimeUnit
{
    public RuntimeUnitId Id => DeliveryCache.WriterId;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var info = Open(() => queue.Store.EnsureInitialized(), inFlight: null);
        LogOpened(logger, info.FilePath, info.SqliteVersion, info.JournalMode, info.Synchronous, info.SchemaVersion);

        var batch = new List<WriteRequest>();
        SqliteConnection? connection = null;
        try
        {
            var stopping = stoppingToken.IsCancellationRequested;
            while (true)
            {
                if (!stopping)
                {
                    try
                    {
                        if (!await queue.Requests.WaitToReadAsync(stoppingToken))
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        stopping = true;
                    }
                }

                var records = Collect(batch, 0);
                if (!stopping && batch.Count > 0 && records < queue.Options.MaximumRecordsPerCommit)
                {
                    try
                    {
                        await Task.Delay(queue.Options.CommitInterval, time, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        stopping = true;
                    }

                    Collect(batch, records);
                }

                if (batch.Count == 0)
                {
                    if (stopping)
                    {
                        return;
                    }

                    continue;
                }

                connection ??= Open(queue.Store.OpenWriteConnection, batch);
                if (!Commit(connection, batch))
                {
                    await connection.DisposeAsync();
                    connection = null;
                }

                batch.Clear();
            }
        }
        finally
        {
            foreach (var request in batch)
            {
                request.Complete(DeliveryWriteResult.Failed("发送缓存写者异常退出，本批未提交。"));
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 打开发送缓存；失败时记“存储异常”，让已取出的本批与请求队列中已有的请求失败（计入失败记录数），再重新抛出，由监督者退避重启。
    /// </summary>
    private T Open<T>(Func<T> open, List<WriteRequest>? inFlight)
    {
        try
        {
            return open();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 与提交失败相同：先更新状态、记事件，再通知等待的一方。
            var error = $"发送缓存不可用：{exception.Message}";
            List<WriteRequest> failed = [.. inFlight ?? [], .. queue.TakeQueued()];
            inFlight?.Clear();
            if (queue.RecordUnavailable(error, failed.Sum(request => request.RecordCount)))
            {
                events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.StorageFault, DeliveryCacheOptions.FileName, error);
            }

            foreach (var request in failed)
            {
                request.Complete(DeliveryWriteResult.Failed(error));
            }

            throw;
        }
    }

    /// <summary>取出请求直到凑满条数上限；一个请求不拆分，超过上限的单个请求单独成批。</summary>
    private int Collect(List<WriteRequest> batch, int records)
    {
        while (records < queue.Options.MaximumRecordsPerCommit && queue.Requests.TryRead(out var request))
        {
            batch.Add(request);
            records += request.RecordCount;
        }

        return records;
    }

    private bool Commit(SqliteConnection connection, List<WriteRequest> batch)
    {
        const string savepoint = "request";
        var createdAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        var results = new DeliveryWriteResult[batch.Count];
        var inserted = 0;
        var deleted = 0;
        var rejected = 0;
        try
        {
            using var transaction = connection.BeginTransaction();
            using var insert = DeliveryStore.CreateInsertCommand(connection, transaction);
            using var delete = DeliveryStore.CreateDeleteCommand(connection, transaction);
            for (var i = 0; i < batch.Count; i++)
            {
                transaction.Save(savepoint);
                try
                {
                    results[i] = Execute(batch[i], insert, delete, createdAt);
                    transaction.Release(savepoint);
                }
                catch (SqliteException exception) when (batch[i] is InsertRequest && exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT)
                {
                    transaction.Rollback(savepoint);
                    transaction.Release(savepoint);
                    results[i] = DeliveryWriteResult.Rejected($"数据错误，本请求未写入：{exception.Message}");
                    rejected += batch[i].RecordCount;
                    LogRequestRejected(logger, exception, batch[i].RecordCount);
                }
            }

            transaction.Commit();
            inserted = results.Where(result => result.Committed).Sum(result => result.Seqs.Count);
            deleted = results.Where((result, index) => result.Committed && batch[index] is DeleteRequest).Sum(result => result.AffectedRecords);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 先更新状态、记事件，再通知等待的一方：收到失败结果时，状态中已经是“存储异常”。
            var records = batch.Sum(request => request.RecordCount);
            var error = $"发送缓存写入失败，本批 {records} 条已回滚：{exception.Message}";
            LogCommitFailed(logger, exception, batch.Count, records);
            if (queue.RecordFailed(records, error))
            {
                events.Record(RuntimeEventSeverity.Error, RuntimeEventTypes.StorageFault, DeliveryCacheOptions.FileName, error);
            }

            foreach (var request in batch)
            {
                request.Complete(DeliveryWriteResult.Failed(error));
            }

            return false;
        }

        if (queue.RecordCommitted(inserted, deleted, rejected))
        {
            events.Record(RuntimeEventSeverity.Info, RuntimeEventTypes.StorageRecovered, DeliveryCacheOptions.FileName, "发送缓存写入已恢复。");
        }

        for (var i = 0; i < batch.Count; i++)
        {
            batch[i].Complete(results[i]);
        }

        return true;
    }

    private static DeliveryWriteResult Execute(WriteRequest request, SqliteCommand insert, SqliteCommand delete, long createdAt)
    {
        switch (request)
        {
            case InsertRequest inserting:
                var seqs = new long[inserting.Drafts.Count];
                for (var j = 0; j < seqs.Length; j++)
                {
                    seqs[j] = DeliveryStore.Insert(insert, inserting.Drafts[j], createdAt);
                }

                return DeliveryWriteResult.Inserted(seqs);
            case DeleteRequest deleting:
                var count = 0;
                foreach (var seq in deleting.Seqs)
                {
                    delete.Parameters["$seq"].Value = seq;
                    count += delete.ExecuteNonQuery();
                }

                return DeliveryWriteResult.Deleted(count);
            default:
                throw new InvalidOperationException($"未知的写入请求 {request.GetType().Name}。");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "一个写入请求的 {Records} 条记录违反约束（数据错误），只拒绝该请求，同批其他请求照常提交。")]
    private static partial void LogRequestRejected(ILogger logger, Exception exception, int records);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "发送缓存 {FilePath} 已打开：SQLite {SqliteVersion}，journal_mode={JournalMode}，synchronous={Synchronous}（2 为 FULL），表结构版本 {SchemaVersion}。")]
    private static partial void LogOpened(ILogger logger, string filePath, string sqliteVersion, string journalMode, long synchronous, int schemaVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "发送缓存写入失败，本批 {Requests} 个请求、{Records} 条记录已回滚。")]
    private static partial void LogCommitFailed(ILogger logger, Exception exception, int requests, int records);
}
