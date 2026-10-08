using System.Collections.Immutable;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 统一内部消息（总设计 §6.4）：一次输入采样或接收的结果，发往多个目标时共用同一条。
/// 只含已有使用者的字段；来源元数据（source）、原始报文与字段级时间（fieldTimes）随 T2 的协议输入补充。
/// <list type="bullet">
/// <item>输入标识（inputKey）与设备标识（deviceKey）符合 <see cref="BusinessKeys"/> 的规则；inputKey 只在设备内唯一，两者一起确定数据来源。</item>
/// <item>值与质量都是不可变的有序字典，键按序号比较：多条路径共用一条消息时不会互相影响，写出的顺序也确定。</item>
/// <item>质量是稀疏表示：值中出现的字段默认为 good；bad 的字段不在值中，只在质量中列出原因码，两者的键不重叠，缺值不会变成 0。
/// 既不在值中、也不在质量中的字段表示本条未携带。</item>
/// <item>时间一律为 UTC。采集时间来自网关（gateway 或 gateway-unsynced）时就是接收时间。</item>
/// </list>
/// </summary>
public sealed class EdgeMessage
{
    private static readonly ImmutableSortedDictionary<string, QualityCode> NoQualityIssues =
        ImmutableSortedDictionary.Create<string, QualityCode>(StringComparer.Ordinal);

    public EdgeMessage(
        Guid sourceMessageId,
        string inputId,
        string deviceKey,
        DateTimeOffset collectedAt,
        TimeSource timeSource,
        DateTimeOffset receivedAt,
        IEnumerable<KeyValuePair<string, FieldValue>> values,
        IEnumerable<KeyValuePair<string, QualityCode>>? quality = null)
    {
        if (sourceMessageId == Guid.Empty)
        {
            throw new ArgumentException("消息编号不能为空。", nameof(sourceMessageId));
        }

        BusinessKeys.Validate(inputId, nameof(inputId));
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        RequireUtc(collectedAt, nameof(collectedAt));
        RequireUtc(receivedAt, nameof(receivedAt));
        if (!Enum.IsDefined(timeSource))
        {
            throw new ArgumentOutOfRangeException(nameof(timeSource), timeSource, "未知的时间来源。");
        }

        if (timeSource is TimeSource.Gateway or TimeSource.GatewayUnsynced && collectedAt != receivedAt)
        {
            throw new ArgumentException("采集时间来自网关时应等于接收时间。", nameof(collectedAt));
        }

        ArgumentNullException.ThrowIfNull(values);
        var valueMap = ToSortedDictionary(values, nameof(values));
        var qualityMap = quality is null ? NoQualityIssues : ToSortedDictionary(quality, nameof(quality));
        var unknown = qualityMap.FirstOrDefault(entry => !Enum.IsDefined(entry.Value));
        if (unknown.Key is not null)
        {
            throw new ArgumentException($"字段 {unknown.Key} 的原因码 {(int)unknown.Value} 不是已定义的原因码。", nameof(quality));
        }

        var overlap = valueMap.Keys.FirstOrDefault(qualityMap.ContainsKey);
        if (overlap is not null)
        {
            throw new ArgumentException($"字段 {overlap} 同时出现在值与质量中：bad 的字段不应带值。", nameof(quality));
        }

        SourceMessageId = sourceMessageId;
        InputId = inputId;
        DeviceKey = deviceKey;
        CollectedAt = collectedAt;
        TimeSource = timeSource;
        ReceivedAt = receivedAt;
        Values = valueMap;
        Quality = qualityMap;
    }

    /// <summary>本次采样或接收的本地稳定编号（建议 UUIDv7），发往多个目标时不变。</summary>
    public Guid SourceMessageId { get; }

    /// <summary>产生消息的输入配置的标识（inputKey，设备内唯一），不用名称代替。</summary>
    public string InputId { get; }

    /// <summary>逻辑设备标识。</summary>
    public string DeviceKey { get; }

    /// <summary>数据所代表的时刻（UTC），来源见 <see cref="TimeSource"/>。</summary>
    public DateTimeOffset CollectedAt { get; }

    public TimeSource TimeSource { get; }

    /// <summary>网关拿到这条数据的时间（UTC）：轮询类为读取完成的时刻，推送类为收到消息的时刻。</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>字段值；出现的字段为 good。</summary>
    public ImmutableSortedDictionary<string, FieldValue> Values { get; }

    /// <summary>bad 的字段及原因码。</summary>
    public ImmutableSortedDictionary<string, QualityCode> Quality { get; }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间须为 UTC。", parameterName);
        }
    }

    private static ImmutableSortedDictionary<string, T> ToSortedDictionary<T>(IEnumerable<KeyValuePair<string, T>> entries, string parameterName)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("字段名不能为空。", parameterName);
            }

            if (value is null)
            {
                throw new ArgumentException($"字段 {key} 没有值。", parameterName);
            }

            if (!builder.TryAdd(key, value))
            {
                throw new ArgumentException($"字段 {key} 重复。", parameterName);
            }
        }

        return builder.ToImmutable();
    }
}
