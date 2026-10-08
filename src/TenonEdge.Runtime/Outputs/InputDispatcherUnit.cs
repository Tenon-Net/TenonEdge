using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Outputs;

/// <summary>单输入最多一条在途消息及 32 条冻结记录；故障重建沿用这一状态，不重建稳定编号。</summary>
internal sealed class InputDispatcherState
{
    public EdgeMessage? Message { get; set; }
    public IReadOnlyList<DeliveryDraft>? Drafts { get; set; }
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InputDispatcherUnit(
    RuntimeUnitId inputId, ChannelReader<EdgeMessage> input, IReadOnlyList<IOutputTarget> targets,
    InputDispatcherState state, long revision, DeliveryCache deliveries,
    TimeProvider time, IRuntimeEventSink events) : IRuntimeUnit
{
    public RuntimeUnitId Id { get; } = RuntimeUnitId.Create("delivery.dispatcher", inputId.Key);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (state.Message is null)
            {
                if (!await input.WaitToReadAsync(stoppingToken))
                {
                    state.Drained.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, time, stoppingToken);
                    return;
                }

                if (!input.TryRead(out var message))
                {
                    continue;
                }

                state.Message = message;
            }

            if (state.Drafts is null)
            {
                var drafts = new List<DeliveryDraft>();
                foreach (var target in targets)
                {
                    var id = Guid.CreateVersion7(time.GetUtcNow());
                    try
                    {
                        var output = target.CreateMessage(state.Message, id);
                        drafts.Add(new DeliveryDraft(id, target.ConnectionId, state.Message.SourceMessageId, state.Message.DeviceKey,
                            revision, output.Payload, output.Fingerprint, output.FingerprintSummary,
                            output.Address, output.QualityOfService));
                    }
                    catch (ArgumentException)
                    {
                        events.Record(RuntimeEventSeverity.Warning, "output.rejected", target.Id.ToString(), "报文格式或大小不满足输出约束，本目标未入队。");
                    }
                }

                state.Drafts = drafts;
            }

            if (state.Drafts.Count > 0)
            {
                var result = await deliveries.EnqueueAsync(state.Drafts, stoppingToken);
                if (result.Status == DeliveryWriteStatus.StorageFailed)
                {
                    throw new InvalidOperationException("输入分发未获存储接管，按退避重试同一条消息。");
                }

                if (result.Status == DeliveryWriteStatus.Stopped)
                {
                    return;
                }

                if (result.Status == DeliveryWriteStatus.Rejected)
                {
                    events.Record(RuntimeEventSeverity.Warning, "output.rejected", Id.ToString(), "发送缓存拒绝本条源消息的投递记录。");
                }
            }

            state.Message = null;
            state.Drafts = null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
