using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Runtime.Outputs;

/// <summary>目标内存状态跨监督者重建保留；最多一条发送结果等待提交。</summary>
internal sealed class OutputSenderState
{
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
    private readonly IOutputAdapter _adapter = target.CreateAdapter();
    public RuntimeUnitId Id => target.Id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
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
                events.Record(RuntimeEventSeverity.Info, "output.resuming", Id.ToString(), "凭证变化，重新探测输出目标。");
            }

            if (state.RetryDelay is { } delay)
            {
                var started = time.GetTimestamp();
                while (time.GetElapsedTime(started) < delay)
                {
                    var currentControl = controls?.Read(target.ConnectionId);
                    if (currentControl?.ManuallyPaused == true || currentControl is not null && currentControl.ResumeVersion != state.ResumeVersion)
                    {
                        break;
                    }

                    var remaining = delay - time.GetElapsedTime(started);
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), time, stoppingToken);
                    }
                }

                state.RetryDelay = null;
            }

            var pending = deliveries.ReadPending(target.ConnectionId, 0, 1);
            var head = pending.Count == 0 ? null : pending[0];
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
            var result = await _adapter.SendAsync(new OutputMessage(record.Payload, record.TargetAddress!, record.QualityOfService,
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

    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}
