using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

/// <summary>
/// 统一消息的最小契约（T0-10；总设计 §6.4）：值与质量不可变、按序号排序且互不重叠；时间为 UTC，来自网关的采集时间等于接收时间；
/// 枚举写出的名称与设计一致。
/// </summary>
public sealed class EdgeMessageTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValuesAndQualityAreImmutableSortedAndSeparate()
    {
        var values = new Dictionary<string, FieldValue>
        {
            ["voltage"] = FieldValue.FromSingle(229.5f),
            ["Current"] = FieldValue.FromDecimal(1.25m),
            ["energy"] = FieldValue.FromUInt64(ulong.MaxValue),
        };

        var message = Message(values, new Dictionary<string, QualityCode> { ["power"] = QualityCode.ReadTimeout });
        values["voltage"] = FieldValue.FromSingle(0f);

        // 按序号排序：大写字母排在小写字母之前；调用方之后修改自己的字典不影响消息。
        Assert.Equal(["Current", "energy", "voltage"], message.Values.Keys);
        Assert.Equal("229.5", message.Values["voltage"].ToString());
        Assert.Equal(QualityCode.ReadTimeout, Assert.Single(message.Quality).Value);
        Assert.Empty(Message(values).Quality);
    }

    [Fact]
    public void AFieldCannotBeBothGoodAndBad()
    {
        var exception = Assert.Throws<ArgumentException>(() => Message(
            new Dictionary<string, FieldValue> { ["voltage"] = FieldValue.FromInt64(0) },
            new Dictionary<string, QualityCode> { ["voltage"] = QualityCode.NonFinite }));

        Assert.Contains("voltage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayTimeIsTheReceivedTime()
    {
        var earlier = ReceivedAt.AddSeconds(-30);

        Assert.Throws<ArgumentException>(() => Message([], collectedAt: earlier, timeSource: TimeSource.Gateway));
        Assert.Throws<ArgumentException>(() => Message([], collectedAt: earlier, timeSource: TimeSource.GatewayUnsynced));

        // 设备或上游给出的时间可以早于接收时间，例如重连后补收的积压。
        Assert.Equal(earlier, Message([], collectedAt: earlier, timeSource: TimeSource.Device).CollectedAt);
        Assert.Equal(earlier, Message([], collectedAt: earlier, timeSource: TimeSource.Upstream).CollectedAt);
    }

    [Fact]
    public void TimesMustBeUtc()
    {
        var local = new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() => new EdgeMessage(
            Guid.CreateVersion7(), "input-1", "meter-1", local, TimeSource.Gateway, local, []));
        Assert.Throws<ArgumentException>(() => new EdgeMessage(
            Guid.CreateVersion7(), "input-1", "meter-1", ReceivedAt, TimeSource.Device, local, []));
    }

    [Fact]
    public void IdentifiersAndFieldNamesAreRequired()
    {
        Assert.Throws<ArgumentException>(() => new EdgeMessage(
            Guid.Empty, "input-1", "meter-1", ReceivedAt, TimeSource.Gateway, ReceivedAt, []));
        Assert.Throws<ArgumentException>(() => new EdgeMessage(
            Guid.CreateVersion7(), "", "meter-1", ReceivedAt, TimeSource.Gateway, ReceivedAt, []));
        Assert.Throws<ArgumentException>(() => new EdgeMessage(
            Guid.CreateVersion7(), "input-1", "", ReceivedAt, TimeSource.Gateway, ReceivedAt, []));
        Assert.Throws<ArgumentException>(() => Message(new Dictionary<string, FieldValue> { [""] = FieldValue.FromInt64(1) }));
        Assert.Throws<ArgumentException>(() => Message([new("voltage", null!)]));
        Assert.Throws<ArgumentException>(() => Message([
            new("voltage", FieldValue.FromInt64(1)),
            new("voltage", FieldValue.FromInt64(2)),
        ]));
    }

    [Fact]
    public void UndefinedQualityCodesAreRejected() =>
        Assert.Throws<ArgumentException>(() => Message([], new Dictionary<string, QualityCode> { ["voltage"] = (QualityCode)42 }));

    [Fact]
    public void EnumNamesFollowTheDesign()
    {
        // 名称由契约固定：枚举自带字符串转换器，不配置转换器时也写成这些名称，配置了带命名策略的转换器时同样不变。
        foreach (var options in new[] { new JsonSerializerOptions(), PlainEnums(), CamelCaseEnums() })
        {
            Assert.Equal("\"gateway-unsynced\"", JsonSerializer.Serialize(TimeSource.GatewayUnsynced, options));
            Assert.Equal("\"device\"", JsonSerializer.Serialize(TimeSource.Device, options));
            Assert.Equal("\"nonFinite\"", JsonSerializer.Serialize(QualityCode.NonFinite, options));
            Assert.Equal("\"conversionFailed\"", JsonSerializer.Serialize(QualityCode.ConversionFailed, options));
            Assert.Equal("\"integer\"", JsonSerializer.Serialize(FieldType.Integer, options));
        }
    }

    private static EdgeMessage Message(
        IEnumerable<KeyValuePair<string, FieldValue>> values,
        IEnumerable<KeyValuePair<string, QualityCode>>? quality = null,
        DateTimeOffset? collectedAt = null,
        TimeSource timeSource = TimeSource.Gateway) =>
        new(Guid.CreateVersion7(ReceivedAt), "input-1", "meter-1", collectedAt ?? ReceivedAt, timeSource, ReceivedAt, values, quality);

    private static JsonSerializerOptions PlainEnums() => new() { Converters = { new JsonStringEnumConverter() } };

    private static JsonSerializerOptions CamelCaseEnums() =>
        new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
