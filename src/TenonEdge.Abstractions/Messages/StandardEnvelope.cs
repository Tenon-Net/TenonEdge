using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 标准信封（总设计 §6.5、§9.4“数据报文”）：通用 MQTT/HTTP 输出默认使用的报文结构，与 TenonIoT 的平台报文相同。
/// <code>
/// {"schemaVersion":1,"messageId":"…","sourceMessageId":"…","deviceKey":"meter-001",
///  "collectedAt":"2026-09-28T08:30:00.000Z","timeSource":"gateway",
///  "values":{"voltage":220.5},"quality":{"current":{"status":"bad","reason":"readTimeout"}}}
/// </code>
/// <list type="bullet">
/// <item>messageId 是本次目标投递的 deliveryId：发往不同目标各有自己的编号，重试不重新生成；sourceMessageId 在各目标间相同。</item>
/// <item>values 与 quality 总是写出（可以为空对象），字段按序号排序；数值按 <see cref="FieldValue"/> 的规则写出，同一字段的 JSON 形式不随数值变化。</item>
/// <item>collectedAt 为 RFC 3339 的 UTC 时间，固定三位毫秒（截断），带 Z 后缀。</item>
/// </list>
/// 报文通过 JSON 写入器生成，不用字符串拼接；生成是纯函数，结果在入库时冻结（总设计 §8.2、§11）。
/// </summary>
public static class StandardEnvelope
{
    public const int SchemaVersion = 1;
    public const int MaximumPayloadBytes = 256 * 1024;

    /// <summary>
    /// 编码过程的保守预约：源消息越小，允许的暂存越小；包括缓冲扩容旧/新数组、JSON 转义暂存。
    /// 上限防止单个字段的转义申请随输入任意增长。不是整个 CLR 堆或 ArrayPool 保留量的测量值。
    /// </summary>
    public static long SerializationReservationBytes(long sourceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceBytes);
        return 128L * Math.Min(sourceBytes, MaximumPayloadBytes) + 64 * 1024;
    }

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static void Write(Utf8JsonWriter writer, EdgeMessage message, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("报文编号不能为空。", nameof(messageId));
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("messageId", messageId);
        writer.WriteString("sourceMessageId", message.SourceMessageId);
        writer.WriteString("deviceKey", message.DeviceKey);
        writer.WriteString("collectedAt", FormatTime(message.CollectedAt));
        writer.WriteString("timeSource", NameOf(message.TimeSource));

        writer.WriteStartObject("values");
        foreach (var (field, value) in message.Values)
        {
            writer.WritePropertyName(field);
            value.WriteTo(writer);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("quality");
        foreach (var (field, reason) in message.Quality)
        {
            writer.WriteStartObject(field);
            writer.WriteString("status", "bad");
            writer.WriteString("reason", NameOf(reason));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>生成 UTF-8 编码的报文。</summary>
    public static byte[] Serialize(EdgeMessage message, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(message);
        // 即使每个字符只需一个 UTF-8 字节也已超限，拒绝在 JSON 写入器内部创建巨大的临时转义数组。
        // 不用“字符数乘六”判定报文大小：那会错误拒绝实际未超限的 ASCII/Unicode 报文。
        foreach (var (name, value) in message.Values)
        {
            if (name.Length > MaximumPayloadBytes || (value.Type == FieldType.String && value.ToString().Length > MaximumPayloadBytes))
            { throw TooLarge(); }
        }

        if (message.Quality.Keys.Any(name => name.Length > MaximumPayloadBytes))
        { throw TooLarge(); }
        var buffer = new BoundedBuffer();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, message, messageId);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static ArgumentException TooLarge() => new($"输出报文超过 {MaximumPayloadBytes} 字节。");

    /// <summary>
    /// IBufferWriter 的 sizeHint 是编码最坏情况，而非实际字节数；容许有限的额外暂存，
    /// 以 Advance 的实际字节判定 256 KiB，不能因 sizeHint 过大就误拒绝合法报文。
    /// </summary>
    internal sealed class BoundedBuffer : IBufferWriter<byte>
    {
        private const int MaximumCapacity = 32 * MaximumPayloadBytes + 4096;
        private byte[] _bytes = new byte[1024];
        private int _written;
        public ReadOnlySpan<byte> WrittenSpan => _bytes.AsSpan(0, _written);

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > MaximumPayloadBytes - _written)
            { throw TooLarge(); }
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _bytes.Length - _written);
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _bytes.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _bytes.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            sizeHint = Math.Max(sizeHint, 1);
            if (sizeHint > MaximumCapacity - _written)
            { throw TooLarge(); }
            if (sizeHint <= _bytes.Length - _written)
            { return; }
            var capacity = Math.Min(MaximumCapacity, Math.Max(_written + sizeHint, _bytes.Length * 2));
            Array.Resize(ref _bytes, capacity);
        }
    }

    /// <summary>报文中的时间格式：UTC，固定三位毫秒，带 Z 后缀。</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>与 <see cref="TimeSource"/> 的 JSON 名称相同（单元测试核对两者一致）。</summary>
    public static string NameOf(TimeSource timeSource) => timeSource switch
    {
        TimeSource.Device => "device",
        TimeSource.Upstream => "upstream",
        TimeSource.Gateway => "gateway",
        TimeSource.GatewayUnsynced => "gateway-unsynced",
        _ => throw new ArgumentOutOfRangeException(nameof(timeSource), timeSource, "未知的时间来源。"),
    };

    /// <summary>与 <see cref="QualityCode"/> 的 JSON 名称相同（单元测试核对两者一致）。</summary>
    public static string NameOf(QualityCode reason) => reason switch
    {
        QualityCode.ReadTimeout => "readTimeout",
        QualityCode.DeviceException => "deviceException",
        QualityCode.ReadError => "readError",
        QualityCode.Overflow => "overflow",
        QualityCode.NonFinite => "nonFinite",
        QualityCode.ConversionFailed => "conversionFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的原因码。"),
    };
}
