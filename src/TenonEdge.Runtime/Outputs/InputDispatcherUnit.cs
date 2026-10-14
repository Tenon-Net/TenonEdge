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
    public Dictionary<Guid, (RuntimeUnitId ObjectId, long Sequence)> Diagnostics { get; } = [];
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class InputDispatcherUnit(
    RuntimeUnitId inputId, ChannelReader<EdgeMessage> input, IReadOnlyList<IOutputTarget> targets,
    InputDispatcherState state, long revision, DeliveryCache deliveries,
    TimeProvider time, IRuntimeEventSink events, BudgetedInputChannel? budgetedChannel = null, IReadOnlyDictionary<RuntimeUnitId, FieldMappingPipeline>? mappings = null,
    RuntimeMessageCache? messages = null) : IRuntimeUnit
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
                    var source = state.Message;
                    var invalidFields = 0;
                    var missingFields = 0;
                    try
                    {
                        if (mappings?.TryGetValue(target.Id, out var mapping) == true)
                        {
                            var mapped = mapping.Apply(source);
                            invalidFields = mapped.InvalidFields;
                            missingFields = mapped.MissingFields;
                            CountPath(target, "mappingInvalidFields", mapped.InvalidFields);
                            CountPath(target, "mappingMissingFields", mapped.MissingFields);
                            if (mapped.Message is null)
                            {
                                CountPath(target, "requiredFieldFailures");
                                RejectPath(target, "必填映射字段缺失或转换失败，本路径未入队。");
                                Capture(target, null, null, id, RuntimeMessageOutcome.MappingRejected, invalidFields, missingFields, mapped.FailedStage);
                                continue;
                            }
                            source = mapped.Message;
                        }
                        var output = target.CreateMessage(source, id);
                        drafts.Add(new DeliveryDraft(id, target.ConnectionId, state.Message.SourceMessageId, state.Message.DeviceKey,
                            revision, output.Payload, output.Fingerprint, output.FingerprintSummary,
                            output.Address, output.QualityOfService, output.Headers, protocolMetadata: output.ProtocolMetadata));
                        Capture(target, source, output, id, RuntimeMessageOutcome.Generated, invalidFields, missingFields);
                    }
                    catch (ArgumentException)
                    {
                        RejectPath(target, "映射结果、报文格式或大小不满足输出约束，本目标未入队。");
                        Capture(target, source, null, id, RuntimeMessageOutcome.FormattingRejected, invalidFields, missingFields);
                    }
                }

                state.Drafts = drafts;
                deliveries.OutputTraffic.Generated(drafts);
            }

            var handoff = InputHandoffStatus.Rejected;
            if (state.Drafts.Count > 0)
            {
                DeliveryWriteResult result;
                try
                { result = await deliveries.EnqueueAsync(state.Drafts, stoppingToken); }
                catch (OperationCanceledException)
                {
                    // Request cancellation may race a durable queue commit; do
                    // not report an unobserved outcome as rejected or queued.
                    CompleteDiagnostics(null);
                    throw;
                }
                CompleteDiagnostics(result);
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
                    state.Diagnostics.Clear();
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
            state.Diagnostics.Clear();
        }
    }

    private void Capture(IOutputTarget target, EdgeMessage? mapped, OutputMessage? output, Guid id,
        RuntimeMessageOutcome outcome, int invalidFields, int missingFields, int? failedStage = null)
    {
        if (messages is null)
        { return; }
        try
        {
            var sequence = messages.CaptureOutput(inputId, target.Id, revision, state.Message!, mapped, output,
                new(inputId.Key, id, outcome, invalidFields, missingFields, failedStage));
            if (outcome == RuntimeMessageOutcome.Generated)
            { state.Diagnostics[id] = (target.Id, sequence); }
        }
        // Diagnostic allocation/encoding must never change business admission.
        catch (Exception) { deliveries.PendingCounters.Add("debugCaptureFailures"); }
    }

    private void CompleteDiagnostics(DeliveryWriteResult? result)
    {
        if (messages is null)
        { return; }
        foreach (var (id, record) in state.Diagnostics)
        {
            var outcome = result?.Status switch
            {
                DeliveryWriteStatus.StorageFailed => RuntimeMessageOutcome.StorageFailed,
                DeliveryWriteStatus.Rejected => RuntimeMessageOutcome.DataRejected,
                DeliveryWriteStatus.Stopped => RuntimeMessageOutcome.Stopped,
                DeliveryWriteStatus.Committed when result.StorageProtectionRejectedRecords > 0 => RuntimeMessageOutcome.StorageProtected,
                DeliveryWriteStatus.Committed when result.InsertedDeliveryIds?.Contains(id) == true => RuntimeMessageOutcome.Queued,
                DeliveryWriteStatus.Committed when result.InsertedDeliveryIds is not null && result.QuotaRejectedRecords > 0 => RuntimeMessageOutcome.QuotaRejected,
                _ => RuntimeMessageOutcome.QueueOutcomeUnknown,
            };
            try
            { messages.CompleteOutput(record.ObjectId, record.Sequence, outcome); }
            catch (Exception) { deliveries.PendingCounters.Add("debugCaptureFailures"); }
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
