using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Outputs;

/// <summary>单输入最多一条在途消息及 32 条冻结记录；提交等待期间持有，停止后由写者结果确定释放时机。</summary>
internal sealed class InputDispatcherState
{
    public EdgeMessage? Message { get; set; }
    public IReadOnlyList<DeliveryDraft>? Drafts { get; set; }
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InputDispatcherUnit(
    RuntimeUnitId inputId, ChannelReader<EdgeMessage> input, IReadOnlyList<IOutputTarget> targets,
    InputDispatcherState state, long revision, DeliveryCache deliveries,
    TimeProvider time, IRuntimeEventSink events, BudgetedInputChannel? budgetedChannel = null) : IRuntimeUnit
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
                        deliveries.PendingCounters.RecordActivity(RuntimeActivity.RejectedPath);
                        deliveries.PendingCounters.Add($"path/{inputId.Key}/{target.ConnectionId}/dataRejected");
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
                    // T1 的轮询/模拟输入按总设计 §8.5：接管前丢失的是这次采样，不无限重试旧数据。
                    // T2 的 MQTT QoS 1 输入须以其确认契约断开并让 Broker 重投，不能复用本分支假装确认。
                    deliveries.PendingCounters.Add("storageLostSourceMessages");
                    deliveries.PendingCounters.Add("storageLostDeliveryRecords", state.Drafts.Count);
                    events.Record(RuntimeEventSeverity.Error, "input.storageLoss", inputId.ToString(),
                        $"发送缓存提交失败，本次采样的 {state.Drafts.Count} 条投递记录未被接管并已丢失。");
                }

                if (result.Status == DeliveryWriteStatus.Stopped)
                {
                    return;
                }

                if (result.Status == DeliveryWriteStatus.Rejected || result.QuotaRejectedRecords > 0 || result.StorageProtectionRejectedRecords > 0)
                {
                    events.Record(RuntimeEventSeverity.Warning, "output.rejected", Id.ToString(), "发送缓存拒绝本条源消息的投递记录。");
                }
            }

            budgetedChannel?.Release(state.Message);
            state.Message = null;
            state.Drafts = null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
