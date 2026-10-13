using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Runtime.Outputs;

/// <summary>目标内存状态跨监督者重建保留；最多一条发送结果等待提交。</summary>
internal sealed partial class OutputSenderState
{
    private OutputSenderDiagnostics _diagnostics = new("normal", null, null, null, null, null, null, null);
    private BackoffWindow? _backoff;
    private sealed record BackoffWindow(string Version, bool Requested);
    public OutputSenderDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    // 每个正在等待的退避仅一个可合并信号；请求线程不触碰发送者的 RetryDelay/失败计数/暂停状态。
    internal void BeginBackoff(DateTimeOffset retryAt)
    {
        var window = new BackoffWindow(Guid.NewGuid().ToString("N"), false);
        Volatile.Write(ref _backoff, window);
        Volatile.Write(ref _diagnostics, Diagnostics with { State = "backoff", RetryAt = retryAt, PauseReason = null, BackoffVersion = window.Version });
    }

    internal bool RequestImmediateRetry(string version)
    {
        while (Volatile.Read(ref _backoff) is { } current)
        {
            if (current.Version != version)
            { return false; }
            if (current.Requested)
            { return true; }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _backoff, current with { Requested = true }, current), current))
            { return true; }
        }
        return false;
    }

    internal bool ImmediateRetryRequested => Volatile.Read(ref _backoff)?.Requested == true;

    internal void EndBackoff()
    {
        Interlocked.Exchange(ref _backoff, null);
        Volatile.Write(ref _diagnostics, Diagnostics with { BackoffVersion = null });
    }

    internal void ReportUnit(RuntimeUnitId id) => Volatile.Write(ref _diagnostics, Diagnostics with { UnitId = id });

    internal void ReportState(string status, DateTimeOffset? retryAt = null, string? pauseReason = null)
    {
        if (status != "backoff")
        { EndBackoff(); }
        var previous = Diagnostics;
        Volatile.Write(ref _diagnostics, previous with { State = status, RetryAt = retryAt, PauseReason = SafeLength(pauseReason) });
    }

    internal void ReportHead(long? sequence)
    {
        var previous = Diagnostics;
        if (previous.HeadSequence != sequence)
        { Volatile.Write(ref _diagnostics, previous with { HeadSequence = sequence, HeadBlockedSince = null }); }
    }

    internal void ReportResult(OutputSendResult result, DateTimeOffset at)
    {
        var previous = Diagnostics;
        Volatile.Write(ref _diagnostics, previous with
        {
            LastResult = result.Status,
            LastError = SafeLength(result.Error),
            LastResultAt = at,
            HeadBlockedSince = result.Status is OutputSendStatus.Retry or OutputSendStatus.SuspendTarget
                ? previous.HeadBlockedSince ?? at : previous.HeadBlockedSince,
        });
    }

    private static string? SafeLength(string? text) => text?.Length > 500 ? text[..500] : text;

    public long? QuarantiningSeq { get; set; }
    public string? ReconciledFingerprint { get; set; }
    public string? ResumeVersion { get; set; }
    public long? AcknowledgedSeq { get; set; }
    public FailureRequest? PendingFailure { get; set; }
    public long? RecheckHeadSeq { get; set; }
    public int ConsecutiveRejects { get; set; }
    public int ConsecutiveRetries { get; set; }
    public string? SuspendedVersion { get; set; }
    public TimeSpan? RetryDelay { get; set; }

    internal TimeSpan NextRetry(RestartBackoff backoff, TimeSpan? retryAfter)
    {
        ConsecutiveRetries = Math.Min(ConsecutiveRetries + 1, 31);
        var delay = backoff.Delay(ConsecutiveRetries);
        if (retryAfter is { } requested && requested > delay)
        {
            delay = requested;
        }

        return delay > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : delay;
    }
}

internal sealed class OutputSenderUnit(
    IOutputTarget target, OutputSenderState state, DeliveryCache deliveries,
    TimeProvider time, IRuntimeEventSink events, RestartBackoff backoff, IOutputControl? controls = null) : IRuntimeUnit
{
    internal const int PoisonThreshold = 10;
    internal const int RejectThreshold = 20;
    private readonly Lazy<IOutputAdapter> _adapter = new(target.CreateAdapter);
    public RuntimeUnitId Id => target.Id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        state.ReportUnit(Id);
        // 准备草稿不借用运行中的物理连接；只有监督者实际启动本实例后才取得租约。
        _ = _adapter.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (state.AcknowledgedSeq is { } acknowledged)
            {
                await RequireCommitAsync(await deliveries.DeleteAsync([acknowledged], stoppingToken),
                    "已确认记录删除未提交，重试删除而不重发。", stoppingToken);
                state.AcknowledgedSeq = null;
                continue;
            }

            if (state.PendingFailure is { } failure)
            {
                // 每次提交创建独立 completion；重试绝对次数/条件状态转换不会重复计数。
                var request = new FailureRequest(failure.Seq, failure.FailureCount, failure.Error, failure.Terminal, failure.Poison);
                await RequireCommitAsync(await deliveries.RecordFailureAsync(request, stoppingToken),
                    "发送结果未提交，重试状态写入而不重发。", stoppingToken);
                state.PendingFailure = null;
                if (failure.Terminal)
                {
                    events.Record(RuntimeEventSeverity.Warning, "output.failed", Id.ToString(),
                        failure.Poison ? "已识别毒消息，按失败区策略处理。" : "单条报文被拒，按失败区策略处理。");
                }

                continue;
            }

            if (state.QuarantiningSeq is { } quarantineSeq)
            {
                await RequireCommitAsync(await deliveries.QuarantineAsync(quarantineSeq, DeliveryQuarantineReasons.TargetChanged, stoppingToken),
                    "隔离状态未提交，保持停止发送。", stoppingToken);
                state.QuarantiningSeq = null;
                continue;
            }

            // 管理操作只能在原发送结果已持久化后取得队列；暂停期间不释放适配器或创建第二个发送者。
            await state.WaitForQueuePauseAsync(stoppingToken);

            var fingerprint = target.CurrentFingerprint;
            if (state.ReconciledFingerprint != fingerprint)
            {
                await RequireCommitAsync(await deliveries.ReconcileAsync(new Dictionary<long, string> { [target.ConnectionId] = fingerprint },
                    quarantineMissing: false, stoppingToken), "目标身份隔离未提交，保持停止发送。", stoppingToken);
                state.ReconciledFingerprint = fingerprint;
                state.RecheckHeadSeq = null;
            }

            var control = controls?.Read(target.ConnectionId);
            if (control?.ManuallyPaused == true)
            {
                state.ReportState("manuallyPaused", pauseReason: "管理员手动暂停。");
                await Task.Delay(TimeSpan.FromMilliseconds(250), time, stoppingToken);
                continue;
            }

            if (control is not null && control.ResumeVersion != state.ResumeVersion)
            {
                state.ResumeVersion = control.ResumeVersion;
                state.SuspendedVersion = null;
                state.RetryDelay = null;
                state.ConsecutiveRejects = 0;
                state.ConsecutiveRetries = 0;
                state.ReportState("normal");
            }

            if (state.SuspendedVersion is { } suspended)
            {
                if (target.CredentialVersion == suspended)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), time, stoppingToken);
                    continue;
                }

                state.SuspendedVersion = null;
                state.ConsecutiveRejects = 0;
                state.ConsecutiveRetries = 0;
                state.ReportState("normal");
                events.Record(RuntimeEventSeverity.Info, "output.resuming", Id.ToString(), "凭证变化，重新探测输出目标。");
            }

            if (state.RetryDelay is { } delay)
            {
                var started = time.GetTimestamp();
                var queuePause = false;
                state.BeginBackoff(time.GetUtcNow() + delay);
                try
                {
                    while (time.GetElapsedTime(started) < delay)
                    {
                        if (state.QueuePauseRequested)
                        { queuePause = true; break; }
                        var currentControl = controls?.Read(target.ConnectionId);
                        if (currentControl?.ManuallyPaused == true || currentControl is not null && currentControl.ResumeVersion != state.ResumeVersion || state.ImmediateRetryRequested)
                        {
                            break;
                        }

                        var remaining = delay - time.GetElapsedTime(started);
                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), time, stoppingToken);
                        }
                    }
                }
                finally { state.EndBackoff(); }

                if (queuePause)
                {
                    // 清空不等同“立即重试”，也不解除退避。暂停结束后接着等待剩余时间。
                    var remaining = delay - time.GetElapsedTime(started);
                    state.RetryDelay = remaining > TimeSpan.Zero ? remaining : null;
                    continue;
                }
                state.RetryDelay = null;
                state.ReportState("normal");
            }

            var pending = deliveries.ReadPending(target.ConnectionId, 0, 1);
            var head = pending.Count == 0 ? null : pending[0];
            state.ReportHead(head?.Seq);
            if (head is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), time, stoppingToken);
                continue;
            }

            var rechecking = state.RecheckHeadSeq == head.Seq;
            var record = !rechecking && head.FailureCount >= PoisonThreshold
                ? deliveries.ReadPoisonProbe(target.ConnectionId, head.Seq, PoisonThreshold) ?? head
                : head;
            state.RecheckHeadSeq = null;
            if (record.Fingerprint != target.CurrentFingerprint)
            {
                state.QuarantiningSeq = record.Seq;
                continue;
            }

            // 退避等待期间可能收到手动暂停；发送前再读一次。
            if (controls?.Read(target.ConnectionId).ManuallyPaused == true)
            {
                continue;
            }

            var version = target.CredentialVersion;
            var result = await SendAsync(record, stoppingToken);
            if (result.FingerprintMismatch)
            {
                state.QuarantiningSeq = record.Seq;
                continue;
            }

            state.ReportResult(result, time.GetUtcNow());
            deliveries.OutputTraffic.Sent(target.ConnectionId, result.Status);
            deliveries.PendingCounters.RecordActivity(result.Status == OutputSendStatus.Ack
                ? RuntimeActivity.Sent : RuntimeActivity.SendFailure);

            switch (result.Status)
            {
                case OutputSendStatus.Ack:
                    state.AcknowledgedSeq = record.Seq;
                    state.ConsecutiveRejects = 0;
                    state.ConsecutiveRetries = 0;
                    if (record.Seq != head.Seq)
                    {
                        state.RecheckHeadSeq = head.Seq;
                    }

                    break;
                case OutputSendStatus.Retry:
                    var count = result.CountsTowardPoison ? Math.Min(record.FailureCount, int.MaxValue - 1) + 1 : record.FailureCount;
                    var poison = rechecking && result.CountsTowardPoison;
                    state.PendingFailure = new(record.Seq, count, result.Error ?? "输出暂时失败。", poison, poison);
                    if (!poison)
                    {
                        state.RetryDelay = state.NextRetry(backoff, result.RetryAfter);
                        state.ReportState("backoff", time.GetUtcNow() + state.RetryDelay.Value);
                        events.Record(RuntimeEventSeverity.Warning, "output.retry", Id.ToString(),
                            result.Error ?? "输出暂时失败，目标退避后重试。");
                    }

                    break;
                case OutputSendStatus.RejectMessage:
                    state.PendingFailure = new(record.Seq, record.FailureCount, result.Error ?? "报文被目标拒绝。", true, false);
                    state.ConsecutiveRejects++;
                    if (state.ConsecutiveRejects >= RejectThreshold)
                    {
                        Suspend(version, "同一目标连续 20 条报文被拒，目标暂停。");
                    }

                    break;
                case OutputSendStatus.SuspendTarget:
                    state.PendingFailure = new(record.Seq, record.FailureCount, result.Error ?? "目标暂停。", false, false);
                    Suspend(version, result.Error ?? "输出目标暂停，记录保持待发。");
                    break;
            }
        }
    }

    private async Task<OutputSendResult> SendAsync(StoredDelivery record, CancellationToken token)
    {
        try
        {
            var result = await _adapter.Value.SendAsync(new OutputMessage(record.Payload, record.TargetAddress!, record.QualityOfService,
                record.Fingerprint, record.FingerprintSummary, record.Headers, record.ProtocolMetadata), token);
            return Enum.IsDefined(result.Status) ? result : new(OutputSendStatus.Retry, "输出返回未知结果。", CountsTowardPoison: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 不记录原异常文本：未知适配器异常可能含 URL、用户名或凭证。
            return exception is OperationCanceledException or TimeoutException or IOException or System.Net.Sockets.SocketException
                ? new(OutputSendStatus.Retry, "输出连接中断或超时。")
                : new(OutputSendStatus.Retry, "输出发生本地未知异常。", CountsTowardPoison: true);
        }
    }

    private void Suspend(string version, string error)
    {
        state.SuspendedVersion = version;
        state.ReportState("automaticallyPaused", pauseReason: error);
        events.Record(RuntimeEventSeverity.Error, "output.suspended", Id.ToString(), error);
    }

    private async Task RequireCommitAsync(DeliveryWriteResult result, string error, CancellationToken token)
    {
        if (result.Status == DeliveryWriteStatus.Stopped)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
        }

        if (!result.Committed)
        {
            throw new InvalidOperationException(error);
        }
    }

    public ValueTask DisposeAsync() => _adapter.IsValueCreated ? _adapter.Value.DisposeAsync() : ValueTask.CompletedTask;
}
