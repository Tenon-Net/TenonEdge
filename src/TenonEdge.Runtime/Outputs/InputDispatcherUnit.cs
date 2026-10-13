using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

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
    TimeProvider time, IRuntimeEventSink events, BudgetedInputChannel? budgetedChannel = null, IReadOnlyDictionary<RuntimeUnitId, FieldMappingPipeline>? mappings = null) : IRuntimeUnit
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
                        var source = state.Message;
                        if (mappings?.TryGetValue(target.Id, out var mapping) == true)
                        {
                            var mapped = mapping.Apply(source);
                            CountPath(target, "mappingInvalidFields", mapped.InvalidFields);
                            CountPath(target, "mappingMissingFields", mapped.MissingFields);
                            if (mapped.Message is null)
                            {
                                CountPath(target, "requiredFieldFailures");
                                RejectPath(target, "必填映射字段缺失或转换失败，本路径未入队。");
                                continue;
                            }
                            source = mapped.Message;
                        }
                        var output = target.CreateMessage(source, id);
                        drafts.Add(new DeliveryDraft(id, target.ConnectionId, state.Message.SourceMessageId, state.Message.DeviceKey,
                            revision, output.Payload, output.Fingerprint, output.FingerprintSummary,
                            output.Address, output.QualityOfService, output.Headers, protocolMetadata: output.ProtocolMetadata));
                    }
                    catch (ArgumentException)
                    {
                        RejectPath(target, "映射结果、报文格式或大小不满足输出约束，本目标未入队。");
                    }
                }

                state.Drafts = drafts;
                deliveries.OutputTraffic.Generated(drafts);
            }

            var handoff = InputHandoffStatus.Rejected;
            if (state.Drafts.Count > 0)
            {
                var result = await deliveries.EnqueueAsync(state.Drafts, stoppingToken);
                handoff = result.Status switch
                {
                    DeliveryWriteStatus.Committed => result.Seqs.Count > 0 ? InputHandoffStatus.Committed : InputHandoffStatus.Rejected,
                    DeliveryWriteStatus.Rejected => InputHandoffStatus.Rejected,
                    DeliveryWriteStatus.StorageFailed => InputHandoffStatus.StorageFailed,
                    _ => InputHandoffStatus.Stopped,
                };
                if (result.Status == DeliveryWriteStatus.StorageFailed && budgetedChannel?.HasHandoff(state.Message) == true)
                {
                    deliveries.PendingCounters.Add($"input/{inputId.Key}/handoffFailed");
                    events.Record(RuntimeEventSeverity.Error, "input.handoffFailed", inputId.ToString(), "发送缓存未提交，可重投输入未被接管，不允许确认上游。");
                }
                else if (result.Status == DeliveryWriteStatus.StorageFailed)
                {
                    // T1 的轮询/模拟输入按总设计 §8.5：接管前丢失的是这次采样，不无限重试旧数据。
                    deliveries.PendingCounters.Add("storageLostSourceMessages");
                    deliveries.PendingCounters.Add("storageLostDeliveryRecords", state.Drafts.Count);
                    events.Record(RuntimeEventSeverity.Error, "input.storageLoss", inputId.ToString(),
                        $"发送缓存提交失败，本次采样的 {state.Drafts.Count} 条投递记录未被接管并已丢失。");
                }

                if (result.Status == DeliveryWriteStatus.Stopped)
                {
                    budgetedChannel?.Release(state.Message, InputHandoffStatus.Stopped);
                    state.Message = null;
                    state.Drafts = null;
                    return;
                }

                if (result.Status == DeliveryWriteStatus.Rejected || result.QuotaRejectedRecords > 0 || result.StorageProtectionRejectedRecords > 0)
                {
                    events.Record(RuntimeEventSeverity.Warning, "output.rejected", Id.ToString(), "发送缓存拒绝本条源消息的投递记录。");
                }
            }

            else
            {
                deliveries.PendingCounters.Add($"input/{inputId.Key}/noDeliveryMessages");
            }

            budgetedChannel?.Release(state.Message, handoff);
            state.Message = null;
            state.Drafts = null;
        }
    }

    private void RejectPath(IOutputTarget target, string reason)
    {
        deliveries.PendingCounters.RecordActivity(RuntimeActivity.RejectedPath);
        CountPath(target, "dataRejected");
        events.Record(RuntimeEventSeverity.Warning, "output.rejected", target.Id.ToString(), reason);
    }

    private void CountPath(IOutputTarget target, string counter, long value = 1)
    {
        // 保留旧连接汇总，并以输出定义标识区分同连接上的路径；不重复计全局 activity。
        deliveries.PendingCounters.Add($"path/{inputId.Key}/{target.ConnectionId}/{counter}", value);
        deliveries.PendingCounters.Add($"route/{inputId.Key}/{target.Id}/{counter}", value);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
