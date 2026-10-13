using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>不可变的输出定义。格式化只做本地计算；每次构建发送实例不做网络 I/O。</summary>
public interface IOutputTarget
{
    RuntimeUnitId Id { get; }
    long ConnectionId { get; }
    IReadOnlyList<string> Inputs { get; }
    string? ClientId { get; }
    string Recipient { get; }
    string CredentialVersion { get; }
    string CurrentFingerprint { get; }
    /// <summary>一次捕获同代的指纹与非秘密接收方；不解密、不连接。未实现的扩展默认不可核对，不猜测身份。</summary>
    OutputTargetIdentity? CaptureIdentity() => null;
    /// <summary>单次发送的期限。管理迁移等待原在途结果落库，不按宿主默认停止期限截断较长的合法发送。</summary>
    TimeSpan SendTimeout => TimeSpan.FromSeconds(5);
    /// <summary>输入入队前预留的每路径格式化上界；默认包含报文及冻结拷贝，协议可增加非秘密元数据暂存。</summary>
    long FormattingReservationBytes => 2L * StandardEnvelope.MaximumPayloadBytes;
    /// <summary>字段组装可能放大很小的输入；各路径串行复用的编码暂存下限。</summary>
    long MinimumSerializationReservationBytes => 0;
    OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId);
    IOutputAdapter CreateAdapter();
    /// <summary>两个路径能否安全共用同一发送者；协议须核对全部连接参数，发送必须使用各记录冻结的地址与 QoS。</summary>
    bool CanShareSenderWith(IOutputTarget other) => false;
    /// <summary>发送者与本计划输入共用接收会话；输入排空后必须释放它，才能切换/恢复新计划范围的会话。
    /// 独立输出默认继续发送，不随输入静默停止。</summary>
    bool QuiesceWithInputs => false;
}

/// <summary>由一个被监督的发送者独占；循环结束之后才释放。协议类型不越过适配器边界。</summary>
public interface IOutputAdapter : IAsyncDisposable
{
    Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken);
}

public sealed record OutputMessage(ReadOnlyMemory<byte> Payload, string Address, int? QualityOfService, string Fingerprint, string FingerprintSummary,
    IReadOnlyDictionary<string, string>? Headers = null, string? ProtocolMetadata = null)
{
    /// <summary>协议专属的冻结请求约定，只含非秘密数据；由适配器解析，不作为 HTTP 请求头发送。</summary>
    public const int MaximumProtocolMetadataBytes = 16 * 1024;
}

public enum OutputSendStatus { Ack, Retry, RejectMessage, SuspendTarget }

/// <summary>错误说明必须为不含凭证/原始响应正文的安全文本。只有明确的非临时失败才计入毒消息次数。</summary>
public sealed record OutputSendResult(
    OutputSendStatus Status,
    string? Error = null,
    bool CountsTowardPoison = false,
    TimeSpan? RetryAfter = null,
    bool FingerprintMismatch = false);

public interface IOutputAdapterFactory
{
    string Kind { get; }
    IOutputTarget Build(RuntimeUnitPlan plan);
    /// <summary>可能共享输入会话的稳定恢复组；只有计划存在同组输入时才共同恢复。只读静态配置，不做 I/O。</summary>
    RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => null;
}
