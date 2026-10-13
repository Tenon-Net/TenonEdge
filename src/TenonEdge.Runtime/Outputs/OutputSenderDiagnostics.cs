using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Outputs;

/// <summary>发送循环发布的不可变诊断快照；不暴露活跃适配器、凭证版本、报文或可变发送状态。
/// 队头阻塞时间从该队头首次发送失败算起，不把创建时间误当阻塞起点。</summary>
public sealed record OutputSenderDiagnostics(string State, DateTimeOffset? RetryAt, string? PauseReason,
    OutputSendStatus? LastResult, string? LastError, DateTimeOffset? LastResultAt,
    long? HeadSequence, DateTimeOffset? HeadBlockedSince, RuntimeUnitId? UnitId = null);
public sealed record RuntimeOutputSender(long ConnectionId, OutputSenderDiagnostics Sender);
