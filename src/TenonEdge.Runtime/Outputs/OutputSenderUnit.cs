using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Outputs;

/// <summary>确认但尚未删库的编号属于目标，跨发送者重建保留；单条在途使内存上限为 1。</summary>
internal sealed class OutputSenderState
{
    public long? AcknowledgedSeq { get; set; }
}

internal sealed class OutputSenderUnit(
    IOutputTarget target, OutputSenderState state, DeliveryCache deliveries,
    TimeProvider time, IRuntimeEventSink events) : IRuntimeUnit
{
    private readonly IOutputAdapter _adapter = target.CreateAdapter();
    public RuntimeUnitId Id => target.Id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (state.AcknowledgedSeq is { } acknowledged)
            {
                var deleted = await deliveries.DeleteAsync([acknowledged], stoppingToken);
                if (deleted.Status == DeliveryWriteStatus.Stopped)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, time, stoppingToken);
                }

                if (!deleted.Committed)
                {
                    throw new InvalidOperationException("已确认记录删除未提交，重试删除而不重发。");
                }

                state.AcknowledgedSeq = null;
                continue;
            }

            var pending = deliveries.ReadPending(target.ConnectionId, 0, 1);
            if (pending.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), time, stoppingToken);
                continue;
            }

            var record = pending[0];
            var version = target.CredentialVersion;
            var result = await _adapter.SendAsync(new OutputMessage(record.Payload, record.TargetAddress!, record.QualityOfService!.Value,
                record.Fingerprint, record.FingerprintSummary), stoppingToken);
            switch (result.Status)
            {
                case OutputSendStatus.Ack:
                    state.AcknowledgedSeq = record.Seq;
                    break;
                case OutputSendStatus.Retry:
                    throw new InvalidOperationException(result.Error ?? "输出临时失败，记录保持待发。");
                case OutputSendStatus.RejectMessage:
                case OutputSendStatus.SuspendTarget:
                    events.Record(RuntimeEventSeverity.Error, "output.suspended", Id.ToString(), result.Error ?? "输出目标暂停，记录保持待发。");
                    while (target.CredentialVersion == version)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), time, stoppingToken);
                    }

                    events.Record(RuntimeEventSeverity.Info, "output.resuming", Id.ToString(), "凭证变化，重新探测输出目标。");
                    break;
            }
        }
    }

    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}
