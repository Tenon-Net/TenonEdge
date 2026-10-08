using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 一条待入库的投递记录（总设计 §8.2）：报文与目标在生成时冻结，入库之后不再按新配置重新生成。
/// <list type="bullet">
/// <item>deliveryId 在生成报文之前分配，报文中的 messageId 就是它（总设计 §9.4）；重试沿用同一编号。</item>
/// <item>冻结的目标：topic 或 path、QoS 与非秘密请求头。凭证不写入记录，发送时从当前连接解析。</item>
/// <item>目标指纹及其可读摘要（总设计 §8.3）由输出适配器按连接计算，发送时与连接当前的指纹比较（T1-06）。</item>
/// </list>
/// </summary>
public sealed class DeliveryDraft
{
    /// <summary>单条输出报文的上限（总设计 §8.4：256 KiB，超限明确失败）。</summary>
    public const int MaximumPayloadBytes = StandardEnvelope.MaximumPayloadBytes;

    public DeliveryDraft(
        Guid deliveryId,
        long connectionId,
        Guid sourceMessageId,
        string deviceKey,
        long revision,
        ReadOnlyMemory<byte> payload,
        string fingerprint,
        string fingerprintSummary,
        string? targetAddress = null,
        int? qualityOfService = null,
        IReadOnlyDictionary<string, string>? headers = null,
        long? routeId = null,
        string? nodeLabel = null)
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("投递编号不能为空。", nameof(deliveryId));
        }

        if (sourceMessageId == Guid.Empty)
        {
            throw new ArgumentException("源消息编号不能为空。", nameof(sourceMessageId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"报文长度应在 1 到 {MaximumPayloadBytes} 字节之间。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintSummary);
        if (qualityOfService is not (null or 0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(qualityOfService), qualityOfService, "QoS 只能为 0 或 1（总设计 §6.5）。");
        }

        if (headers is not null && headers.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("请求头名称不能为空。", nameof(headers));
        }

        DeliveryId = deliveryId;
        ConnectionId = connectionId;
        SourceMessageId = sourceMessageId;
        DeviceKey = deviceKey;
        Revision = revision;
        Payload = payload.ToArray();
        Fingerprint = fingerprint;
        FingerprintSummary = fingerprintSummary;
        TargetAddress = targetAddress;
        QualityOfService = qualityOfService;
        Headers = headers?.ToImmutableSortedDictionary(StringComparer.Ordinal);
        RouteId = routeId;
        NodeLabel = nodeLabel;
    }

    /// <summary>配额的逻辑字节：冻结正文及 UTF-8 文本，加 256 字节固定记录开销；不等于 SQLite/WAL 文件尺寸。</summary>
    public long ByteSize => Payload.Length + 256L + Encoding.UTF8.GetByteCount(DeviceKey)
        + Encoding.UTF8.GetByteCount(NodeLabel ?? string.Empty) + Encoding.UTF8.GetByteCount(TargetAddress ?? string.Empty)
        + Encoding.UTF8.GetByteCount(Fingerprint) + Encoding.UTF8.GetByteCount(FingerprintSummary)
        + (Headers is null ? 0 : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Headers)));

    public Guid DeliveryId { get; }

    /// <summary>投递目标即连接（总设计 §8.2）：队列分区、发送者、退避、配额与指纹都以它为单位。</summary>
    public long ConnectionId { get; }

    public Guid SourceMessageId { get; }

    public string DeviceKey { get; }

    /// <summary>生成该记录的配置版本号：应用的激活边界据此核对（AT-28）。</summary>
    public long Revision { get; }

    /// <summary>冻结的报文（UTF-8），由调用方复制一份保存，之后不受调用方的缓冲区影响。</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public string Fingerprint { get; }

    public string FingerprintSummary { get; }

    /// <summary>冻结的 MQTT topic 或 HTTP path。</summary>
    public string? TargetAddress { get; }

    public int? QualityOfService { get; }

    /// <summary>冻结的非秘密请求头，按名称排序。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>路线与节点只作为统计标签（总设计 §8.2）。</summary>
    public long? RouteId { get; }

    public string? NodeLabel { get; }
}
