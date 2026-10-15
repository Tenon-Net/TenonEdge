using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
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

    public DeliveryCache(DeliveryCacheOptions options, TimeProvider time, Func<StorageSpace?>? readSpace = null, TenonEdge.Abstractions.Contracts.IRuntimeEventSink? events = null, IDeliveryPolicySource? policy = null)
    {
        Options = options.Validate();
        _time = time;
        OutputTraffic = new OutputTrafficHistory(time);
        PolicySource = policy ?? new FixedDeliveryPolicySource(options.ToPolicy());
        Store = new DeliveryStore(options, time, events, PolicySource);
        StorageProtection = new StorageProtection(Path.GetDirectoryName(options.DatabasePath)!,
            options.MinimumDiskFreeBytes, options.MinimumDiskFreeFraction, readSpace, () =>
            {
                var current = PolicySource.Read();
                return (current.MinimumDiskFreeBytes, current.MinimumDiskFreeFraction);
            });
        _requests = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(options.RequestQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    internal IDeliveryPolicySource PolicySource { get; }
    public DeliveryPolicy CurrentPolicy => PolicySource.Read();
    public StorageProtection StorageProtection { get; }

    /// <summary>MQTT 接收器的有限诊断计数，由原有唯一写者合并持久化；不逐条写运行事件或另开事务。</summary>
    public void RecordMqttInputDiagnostic(long connectionId, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(connectionId, 9007199254740991L);
        if (reason is not ("quiescing" or "subscribed" or "unsubscribed" or "sessionLost" or "acknowledged" or "oversized" or "wireOversized" or "bufferDropped" or "bufferFull" or "bufferFailed" or "retainedIgnored" or "unmatched" or "handoffTimeout" or "handoffFailed"))
        { throw new ArgumentException("未知的 MQTT 输入诊断类型。", nameof(reason)); }
        PendingCounters.Add($"connection/{connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/mqtt/{reason}");
    }

    public IReadOnlyDictionary<string, long> GetPersistentCounters() => Store.ReadCounters();
    public IReadOnlyDictionary<string, long> GetUnpersistedCounters() => PendingCounters.Snapshot();
    public RuntimeActivityTotals GetActivityTotals() => PendingCounters.ActivitySnapshot();
    internal PendingCounters PendingCounters { get; } = new();

    public DeliveryIdentityBarrier IdentityBarrier { get; } = new();

    public bool HasUndelivered(long connectionId) => Store.HasUndelivered(connectionId);

    public DeliveryCacheOptions Options { get; }
    public OutputTrafficHistory OutputTraffic { get; }

    internal DeliveryStore Store { get; }

    internal ChannelReader<WriteRequest> Requests => _requests.Reader;

    /// <summary>
    /// 把一组记录交给写者，等待它们所在的分组提交返回。符合配额的记录在同一事务中接管；超额记录只计拒绝，结果通过 QuotaRejectedRecords 明示。
    /// 业务生成者须在冻结身份之前预约 IdentityBarrier，并持有至接管结果确定；仅在此处预约无法保护已冻结的旧身份。
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

    public DeliveryQueueReport GetQueueReport(IReadOnlyCollection<long> activeConnections, long after = 0, int limit = 100) =>
        Store.ReadQueueReport(activeConnections, after, limit);

    public DeliveryRecoveryCheck CheckRecoveryTargets(Func<long, string, string, string?, bool> allows, CancellationToken token) =>
        Store.CheckRecoveryTargets(allows, token);

    public DeliveryInspectionPage Inspect(DeliveryInspectionQuery query) => Store.Inspect(query);

    /// <summary>管理员迁移选择专用，允许查看待发元数据；不改变普通故障列表的只读边界。</summary>
    public DeliveryInspectionPage InspectForMigration(DeliveryInspectionQuery query) => Store.Inspect(query, allowPending: true);

    public IReadOnlyList<DeliveryMigrationCandidate> ReadMigrationCandidates(long connectionId, IReadOnlyList<DeliveryMigrationKey> keys) =>
        Store.ReadMigrationCandidates(connectionId, keys);

    public DeliveryMigrationPreflight PreflightMigration(DeliveryMigrationCommand command) => Store.PreflightMigration(command);

    /// <summary>调用方必须先执行正文权限校验。仅查看指定失败/隔离区中仍存在的稳定投递编号。</summary>
    public DeliveryBodyPreview? InspectBody(Guid deliveryId, DeliveryState state) => Store.InspectBody(deliveryId, state);

    /// <summary>仅供已授权管理员核对整个导出范围；不读正文、不写队列、不保留数据库事务。</summary>
    public DeliveryExportReview ReviewExport(DeliveryExportFilter filter, CancellationToken cancellationToken) =>
        Store.ReviewExport(filter, cancellationToken);

    /// <summary>仅供已确认的管理员操作，把完整 JSON Lines 写入调用方的空私有暂存文件。
    /// 不得边生成边下载：仅返回 true 后调用方才可发布文件；false、取消或异常必须丢弃暂存文件。
    /// 不拥有或关闭流；调用方负责磁盘余量、flush/关闭、授权、审计、下载与残留清理。</summary>
    public Task<bool> WriteExportSpoolAsync(DeliveryExportReview review, Stream spool, CancellationToken cancellationToken) =>
        Store.WriteExportSpoolAsync(review, spool, cancellationToken);

    /// <summary>授权的管理调用方提供已选择的失败记录代次及当前有效目标指纹。
    /// 全部记录匹配且配额足够时，由唯一写者原子移到队尾；旧 seq 不复用，重复提交不能再次入队。
    /// 不解除目标暂停、修改目标身份或生成报文；取消等待不撤销已交给写者的操作。</summary>
    public Task<DeliveryWriteResult> RetryFailedAsync(IReadOnlyList<DeliveryRetryItem> items,
        IReadOnlyDictionary<long, string> targetFingerprints, CancellationToken cancellationToken) =>
        SubmitAsync(new RetryFailedRequest(items, targetFingerprints), cancellationToken);

    /// <summary>仅供已授权且已确认的管理操作。整批删除明确选择的失败/隔离记录，独立累计人工清理数量；
    /// 存储保护期间也允许清理，不重发、不删除待发数据、不等同协议确认。取消只结束等待。</summary>
    public Task<DeliveryWriteResult> CleanupAsync(DeliveryCleanupSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return SubmitAsync(new CleanupRequest(selection), cancellationToken);
    }

    public DeliveryQueueTotals GetQueueTotals(long connectionId) => Store.ReadQueueTotals(connectionId);

    // 仅由运行时在持有原发送者暂停租约时调用，不能直接开放为管理 API。
    internal Task<DeliveryWriteResult> ClearPendingAsync(long connectionId) =>
        SubmitAsync(new ClearPendingRequest(connectionId), CancellationToken.None);

    // 生命周期所有者必须持有源与目标的暂停租约直至提交完成；请求进入写者后不取消等待。
    internal Task<DeliveryWriteResult> MigrateAsync(DeliveryMigrationCommand command) =>
        SubmitAsync(new MigrateDeliveriesRequest(command), CancellationToken.None);

    /// <summary>当前配置库归属下的持久完成记录；供宿主同步迁移状态/审计，不启动或重放迁移。</summary>
    public DeliveryMigrationReceipt? ReadMigrationReceipt(Guid operationId) => Store.ReadMigrationReceipt(operationId);

    public DeliveryMigrationHoldReceipt? ReadMigrationHold(Guid intentId) => Store.ReadMigrationHold(intentId);

    // 仅原运行时的提交后屏障或启动前隔离入口调用；不能在配置提交之前写入此状态。
    internal Task<DeliveryWriteResult> HoldMigrationAsync(DeliveryMigrationHold command) =>
        SubmitAsync(new HoldMigrationRequest(command), CancellationToken.None);

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

internal sealed class FailureRequest(long seq, int failureCount, string error, bool terminal, bool poison, HttpResponseSummary? responseSummary = null) : WriteRequest
{
    public long Seq { get; } = seq;
    public int FailureCount { get; } = failureCount;
    public string Error { get; } = error.Length <= 1024 ? error : error[..1024];
    public HttpResponseSummary? ResponseSummary { get; } = responseSummary;
    public string? ResponseSummaryJson { get; } = responseSummary is null ? null : JsonSerializer.Serialize(responseSummary);
    public int ResponseSummaryBytes => ResponseSummaryJson is null ? 0 : Encoding.UTF8.GetByteCount(ResponseSummaryJson);
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
