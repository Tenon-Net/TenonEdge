using System.Text.Json;
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
    /// <summary>发送前实时读取指纹与凭证版本；协议可从一次元数据读取生成二者，不缓存到下一次发送。
    /// 默认沿用既有属性，兼容未提供合并读取的扩展；不替代循环开始或适配器内部的身份复核。</summary>
    OutputSendIdentity ReadSendIdentity() => new(CurrentFingerprint, CredentialVersion);
    /// <summary>可选的发送者独占读取范围。只在循环开始取得，退出即释放；不缓存安全状态。
    /// 默认保持原目标路径，扩展包装不自动转发，避免绕过冻结路由校验。</summary>
    IOutputTargetReadSession? OpenSenderReadSession() => null;
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

/// <summary>仅由一个运行中的发送者使用；Target 的适配器及身份核对使用此读取范围，不能用于并发格式化。</summary>
public interface IOutputTargetReadSession : IDisposable
{
    IOutputTarget Target { get; }
}

/// <summary>由一个被监督的发送者独占；循环结束之后才释放。协议类型不越过适配器边界。</summary>
public interface IOutputAdapter : IAsyncDisposable
{
    Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken);
}

/// <summary>一次发送前的非秘密核对值，不是凭证、持久快照或跨发送缓存。</summary>
public sealed record OutputSendIdentity(string Fingerprint, string CredentialVersion);

public sealed record OutputMessage(ReadOnlyMemory<byte> Payload, string Address, int? QualityOfService, string Fingerprint, string FingerprintSummary,
    IReadOnlyDictionary<string, string>? Headers = null, string? ProtocolMetadata = null)
{
    /// <summary>协议专属的冻结请求约定，只含非秘密数据；由适配器解析，不作为 HTTP 请求头发送。</summary>
    public const int MaximumProtocolMetadataBytes = 16 * 1024;
}

/// <summary>纯格式化结果，不包含账户、凭证、指纹或发送状态。适配器的正式 CreateMessage 与预览复用同一实现。
/// 正文为 1 字节—256 KiB 的有效 UTF-8 文本；头和协议元数据必须非秘密、有界；返回后不得修改缓冲区。</summary>
public sealed record OutputMessageContent(ReadOnlyMemory<byte> Payload, string Address, int? QualityOfService = null,
    IReadOnlyDictionary<string, string>? Headers = null, string? ProtocolMetadata = null)
{
    public override string ToString() => "Formatted output (body omitted)";
}

public enum OutputSendStatus { Ack, Retry, RejectMessage, SuspendTarget }

/// <summary>错误说明必须为不含凭证/原始响应正文的安全文本。只有明确的非临时失败才计入毒消息次数。</summary>
public sealed record OutputSendResult(
    OutputSendStatus Status,
    string? Error = null,
    bool CountsTowardPoison = false,
    TimeSpan? RetryAfter = null,
    bool FingerprintMismatch = false)
{
    /// <summary>受保护的诊断正文；不得拼入 Error、输出状态或运行事件。</summary>
    public HttpResponseSummary? ResponseSummary { get; init; }
}

public interface IOutputAdapterFactory
{
    string Kind { get; }
    IOutputTarget Build(RuntimeUnitPlan plan);
    /// <summary>可选的纯格式化入口，须与正式目标 CreateMessage 复用同一实现。
    /// 保留正式 Build 的原运行单元 ID，只接受冻结的非秘密协议配置和已经映射的消息，不再执行映射；不 Build 目标、不读取凭证、不连接或入队。
    /// null 表示未提供纯格式化能力，预览不得猜测协议或调用 Build 代替。实现须有界并响应取消。</summary>
    OutputMessageContent? FormatMessage(RuntimeUnitPlan plan, EdgeMessage message, Guid deliveryId, CancellationToken cancellationToken) => null;
    /// <summary>可选的无路线发送绑定。仅纯本地计算，返回非秘密协议配置；不连接、不读凭证、不生成报文。
    /// 配置须声明 queueOnly=true、空 inputs、原 connectionId 和空地址模板的 transportRoute。
    /// null 表示此传输不提供队列接续。结果在新编译时冻结；历史读取绝不调用该方法。</summary>
    JsonElement? BindQueueOnly(ModuleConnectionSnapshot connection) => null;
    /// <summary>可能共享输入会话的稳定恢复组；只有计划存在同组输入时才共同恢复。只读静态配置，不做 I/O。</summary>
    RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => null;
}
