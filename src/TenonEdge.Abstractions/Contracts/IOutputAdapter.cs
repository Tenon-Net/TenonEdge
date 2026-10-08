using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>不可变的输出定义。格式化只做本地计算；每次构建发送实例不做网络 I/O。</summary>
public interface IOutputTarget
{
    RuntimeUnitId Id { get; }
    long ConnectionId { get; }
    IReadOnlyList<string> Inputs { get; }
    string ClientId { get; }
    string Recipient { get; }
    string CredentialVersion { get; }
    OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId);
    IOutputAdapter CreateAdapter();
}

/// <summary>由一个被监督的发送者独占；循环结束之后才释放。协议类型不越过适配器边界。</summary>
public interface IOutputAdapter : IAsyncDisposable
{
    Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken);
}

public sealed record OutputMessage(ReadOnlyMemory<byte> Payload, string Address, int QualityOfService, string Fingerprint, string FingerprintSummary);

public enum OutputSendStatus { Ack, Retry, RejectMessage, SuspendTarget }

public sealed record OutputSendResult(OutputSendStatus Status, string? Error = null);

public interface IOutputAdapterFactory
{
    string Kind { get; }
    IOutputTarget Build(RuntimeUnitPlan plan);
}
