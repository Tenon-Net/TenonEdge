using System.Threading.Channels;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 发送缓存的入口（总设计 §8.2）：入库、删除等全部写操作进入同一个有界请求队列，由唯一的写者按短间隔或条数分组提交；
/// 读取直接查询已提交的记录。
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> 在包含这批记录的分组提交返回后才报告成功（“接管”，总设计 §8.5）；提交失败时整批回滚，
/// 报告失败，调用方不得当作已接管。一次请求中符合配额的记录与超额目标的拒绝计数在同一事务中提交。</item>
/// <item>请求队列满时提交请求的一方等待，不在内存中无限堆积（总设计 §8.7）。</item>
/// <item>取消等待只结束调用方的等待，不撤回已经交给写者的请求。</item>
/// </list>
/// 失败区、配额、隔离和保留期均经此入口；磁盘存储保护在 T1-07。
/// </summary>
public sealed class DeliveryCache
{
    /// <summary>写者在运行计划中的标识：每个进程只有这一个写者。</summary>
    public static readonly RuntimeUnitId WriterId = RuntimeUnitId.Create("delivery.writer", "queue");

    private readonly Channel<WriteRequest> _requests;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    private bool _storageFault;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;
    private long _committedBatches;
    private long _committedRecords;
    private long _deletedRecords;
    private long _failedBatches;
    private long _failedRecords;
    private long _rejectedRecords;

    public DeliveryCache(DeliveryCacheOptions options, TimeProvider time, Func<StorageSpace?>? readSpace = null, TenonEdge.Abstractions.Contracts.IRuntimeEventSink? events = null)
    {
        Options = options.Validate();
        _time = time;
        Store = new DeliveryStore(options, time, events);
        StorageProtection = new StorageProtection(Path.GetDirectoryName(options.DatabasePath)!,
            options.MinimumDiskFreeBytes, options.MinimumDiskFreeFraction, readSpace);
        _requests = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(options.RequestQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public StorageProtection StorageProtection { get; }

    /// <summary>MQTT 接收器的有限诊断计数，由原有唯一写者合并持久化；不逐条写运行事件或另开事务。</summary>
    public void RecordMqttInputDiagnostic(long connectionId, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(connectionId, 9007199254740991L);
        if (reason is not ("quiescing" or "subscribed" or "sessionLost" or "acknowledged" or "oversized" or "retainedIgnored" or "unmatched" or "handoffTimeout" or "handoffFailed"))
        { throw new ArgumentException("未知的 MQTT 输入诊断类型。", nameof(reason)); }
        PendingCounters.Add($"connection/{connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/mqtt/{reason}");
    }

    public IReadOnlyDictionary<string, long> GetPersistentCounters() => Store.ReadCounters();
    public IReadOnlyDictionary<string, long> GetUnpersistedCounters() => PendingCounters.Snapshot();
    public RuntimeActivityTotals GetActivityTotals() => PendingCounters.ActivitySnapshot();
    internal PendingCounters PendingCounters { get; } = new();

    public DeliveryCacheOptions Options { get; }

    internal DeliveryStore Store { get; }

    internal ChannelReader<WriteRequest> Requests => _requests.Reader;

    /// <summary>
    /// 把一组记录交给写者，等待它们所在的分组提交返回。符合配额的记录在同一事务中接管；超额记录只计拒绝，结果通过 QuotaRejectedRecords 明示。
    /// </summary>
    public async Task<DeliveryWriteResult> EnqueueAsync(IReadOnlyList<DeliveryDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
        {
            throw new ArgumentException("至少需要一条记录。", nameof(drafts));
        }

        if (drafts.Any(draft => draft is null))
        {
            throw new ArgumentException("记录不能为空。", nameof(drafts));
        }

        if (drafts.Select(draft => draft.DeliveryId).Distinct().Count() != drafts.Count)
        {
            throw new ArgumentException("同一请求中的投递编号重复。", nameof(drafts));
        }

        return await SubmitAsync(new InsertRequest([.. drafts]), cancellationToken);
    }

    /// <summary>删除已获得协议确认的记录（总设计 §8.2：确认后在下一次分组提交中删除）；返回实际删除的条数。</summary>
    public async Task<DeliveryWriteResult> DeleteAsync(IReadOnlyList<long> seqs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seqs);
        if (seqs.Count == 0)
        {
            throw new ArgumentException("至少需要一条记录。", nameof(seqs));
        }

        return await SubmitAsync(new DeleteRequest([.. seqs]), cancellationToken);
    }

    /// <summary>按 seq 顺序读取某个目标已提交的待发记录；未提交或已回滚的记录读不到。</summary>
    public IReadOnlyList<StoredDelivery> ReadPending(long connectionId, long afterSeq, int limit) =>
        Store.ReadPending(connectionId, afterSeq, limit);

    /// <summary>失败区分页读取；管理端授权及导出入口在 T3。</summary>
    public IReadOnlyList<StoredDelivery> ReadFailed(long connectionId, long afterSeq, int limit) =>
        Store.Read(connectionId, afterSeq, limit, DeliveryState.Failed);

    public IReadOnlyList<StoredDelivery> ReadQuarantined(long connectionId, long afterSeq, int limit) =>
        Store.Read(connectionId, afterSeq, limit, DeliveryState.Quarantined);

    public DeliveryQueueTotals GetQueueTotals(long connectionId) => Store.ReadQueueTotals(connectionId);

    internal Task<DeliveryWriteResult> QuarantineAsync(long seq, string reason, CancellationToken token) =>
        SubmitAsync(new QuarantineRequest(seq, reason), token);

    internal Task<DeliveryWriteResult> ReconcileAsync(IReadOnlyDictionary<long, string> targets, bool quarantineMissing, CancellationToken token) =>
        SubmitAsync(new ReconcileRequest(targets, quarantineMissing), token);

    public DeliveryFailureTotals GetFailureTotals(long connectionId) => Store.ReadFailureTotals(connectionId);

    internal StoredDelivery? ReadPoisonProbe(long connectionId, long afterSeq, int threshold)
    {
        var records = Store.Read(connectionId, afterSeq, 1, DeliveryState.Pending, threshold);
        return records.Count == 0 ? null : records[0];
    }

    // 使用绝对次数与 pending 条件，使同一结果的提交重试幂等；不重复发送已获得的结果。
    internal Task<DeliveryWriteResult> RecordFailureAsync(FailureRequest request, CancellationToken token) => SubmitAsync(request, token);

    public DeliveryCacheStatus GetStatus()
    {
        lock (_lock)
        {
            return new DeliveryCacheStatus(
                _storageFault,
                _lastError,
                _lastErrorAt,
                _committedBatches,
                _committedRecords,
                _deletedRecords,
                _failedBatches,
                _failedRecords,
                _rejectedRecords,
                Store.Info);
        }
    }

    /// <summary>停机顺序中由运行时调用：不再接受新请求，写者提交已收到的请求后退出。</summary>
    internal void CompleteAdding() => _requests.Writer.TryComplete();

    /// <summary>把请求队列中尚未处理的请求一律报告失败（写者停止之后，或发送缓存打不开时），不让等待的一方一直挂起；返回其中的记录数。</summary>
    internal int FailRemaining(string reason)
    {
        var queued = TakeQueued();
        foreach (var request in queued)
        {
            request.Complete(DeliveryWriteResult.Stopped(reason));
        }

        return queued.Sum(request => request.RecordCount);
    }

    /// <summary>取出请求队列中尚未处理的请求，由调用方决定如何完成。</summary>
    internal List<WriteRequest> TakeQueued()
    {
        var queued = new List<WriteRequest>();
        while (_requests.Reader.TryRead(out var request))
        {
            queued.Add(request);
        }

        return queued;
    }

    /// <summary>写者打不开发送缓存（文件损坏、权限、SQLite 版本等）；返回此前是否正常（用于记“存储异常”事件）。</summary>
    internal bool RecordUnavailable(string error, int failedRecords)
    {
        PendingCounters.RecordActivity(RuntimeActivity.StorageFailedRecordAttempt, failedRecords);
        PendingCounters.Add("storageUnavailableAttempts");
        PendingCounters.Add("storageFailedRecordAttempts", failedRecords);
        lock (_lock)
        {
            var wasHealthy = !_storageFault;
            _storageFault = true;
            _lastError = error;
            _lastErrorAt = _time.GetUtcNow();
            _failedRecords += failedRecords;
            return wasHealthy;
        }
    }

    /// <summary>写者报告一次成功的提交（其中因数据错误被拒绝的记录另计）；返回此前是否处于存储异常（用于记“恢复”事件）。</summary>
    internal bool RecordCommitted(int records, int deleted, int rejected)
    {
        lock (_lock)
        {
            var recovered = _storageFault;
            _storageFault = false;
            _committedBatches++;
            _committedRecords += records;
            _deletedRecords += deleted;
            _rejectedRecords += rejected;
            return recovered;
        }
    }

    /// <summary>写者报告一次失败的提交（已回滚）；返回此前是否正常（用于记“存储异常”事件）。</summary>
    internal bool RecordFailed(int records, string error)
    {
        PendingCounters.RecordActivity(RuntimeActivity.StorageFailedRecordAttempt, records);
        PendingCounters.Add("storageFailedBatches");
        PendingCounters.Add("storageFailedRecordAttempts", records);
        lock (_lock)
        {
            var wasHealthy = !_storageFault;
            _storageFault = true;
            _lastError = error;
            _lastErrorAt = _time.GetUtcNow();
            _failedBatches++;
            _failedRecords += records;
            return wasHealthy;
        }
    }

    private async Task<DeliveryWriteResult> SubmitAsync(WriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _requests.Writer.WriteAsync(request, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return DeliveryWriteResult.Stopped("发送缓存已停止，不再接受写入。");
        }

        return await request.Completion.WaitAsync(cancellationToken);
    }
}

/// <summary>交给写者的一个请求；写者提交或回滚后完成。</summary>
internal abstract class WriteRequest
{
    private readonly TaskCompletionSource<DeliveryWriteResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<DeliveryWriteResult> Completion => _completion.Task;

    /// <summary>本请求涉及的记录数，用于控制一个事务的规模。</summary>
    public abstract int RecordCount { get; }

    public void Complete(DeliveryWriteResult result) => _completion.TrySetResult(result);
}

internal sealed class InsertRequest(IReadOnlyList<DeliveryDraft> drafts) : WriteRequest
{
    public IReadOnlyList<DeliveryDraft> Drafts { get; } = drafts;

    public override int RecordCount => Drafts.Count;
}

internal sealed class DeleteRequest(IReadOnlyList<long> seqs) : WriteRequest
{
    public IReadOnlyList<long> Seqs { get; } = seqs;

    public override int RecordCount => Seqs.Count;
}

internal sealed class FailureRequest(long seq, int failureCount, string error, bool terminal, bool poison) : WriteRequest
{
    public long Seq { get; } = seq;
    public int FailureCount { get; } = failureCount;
    public string Error { get; } = error.Length <= 1024 ? error : error[..1024];
    public bool Terminal { get; } = terminal;
    public bool Poison { get; } = poison;
    public override int RecordCount => 1;
}

internal sealed class QuarantineRequest(long seq, string reason) : WriteRequest
{
    public long Seq { get; } = seq;
    public string Reason { get; } = reason;
    public override int RecordCount => 1;
}

internal sealed class ReconcileRequest(IReadOnlyDictionary<long, string> targets, bool quarantineMissing) : WriteRequest
{
    public IReadOnlyDictionary<long, string> Targets { get; } = new Dictionary<long, string>(targets);
    public bool QuarantineMissing { get; } = quarantineMissing;
    public override int RecordCount => 1;
}
