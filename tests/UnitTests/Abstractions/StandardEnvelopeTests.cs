using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

/// <summary>
/// 标准信封（T1-02；总设计 §6.5、§9.4）：字段与平台报文一致，messageId 为投递编号，数值逐位写出，
/// values 与 quality 总是写出，bad 字段带原因码；时间为固定三位毫秒的 UTC。
/// </summary>
public sealed class StandardEnvelopeTests
{
    private static readonly Guid SourceMessageId = Guid.Parse("071529ee-c6ad-4c56-b9b6-34b0e72e5120");
    private static readonly Guid DeliveryId = Guid.Parse("a4b5e193-090d-4a88-8d19-5b6b7d21f053");
    private static readonly DateTimeOffset At = new(2026, 9, 28, 8, 30, 0, 123, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactPayloadLimitIsAcceptedAndOneMoreByteIsRejected(bool escaped)
    {
        var overhead = StandardEnvelope.Serialize(TextMessage("value", ""), DeliveryId).Length;
        var available = StandardEnvelope.MaximumPayloadBytes - overhead;
        var text = escaped ? new string('\u0001', available / 6) + new string('a', available % 6)
            : new string('a', available);
        var payload = StandardEnvelope.Serialize(TextMessage("value", text), DeliveryId);
        Assert.Equal(StandardEnvelope.MaximumPayloadBytes, payload.Length);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(text, document.RootElement.GetProperty("values").GetProperty("value").GetString());
        Assert.Throws<ArgumentException>(() => StandardEnvelope.Serialize(TextMessage("value", text + "a"), DeliveryId));
    }

    [Fact]
    public void LongLegalPropertyNameIsNotRejectedByWorstCaseSizeHint()
    {
        var overhead = StandardEnvelope.Serialize(TextMessage("k", ""), DeliveryId).Length - 1;
        var name = new string('k', StandardEnvelope.MaximumPayloadBytes - overhead);
        var payload = StandardEnvelope.Serialize(TextMessage(name, ""), DeliveryId);
        Assert.Equal(StandardEnvelope.MaximumPayloadBytes, payload.Length);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("", document.RootElement.GetProperty("values").GetProperty(name).GetString());
        Assert.Throws<ArgumentException>(() => StandardEnvelope.Serialize(TextMessage(name + "k", ""), DeliveryId));
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("\ud800")]
    [InlineData("\udc00")]
    [InlineData("中文\\\"\n")]
    public void SegmentEdgesPreserveDefaultJsonEscaping(string middle)
    {
        var text = new string('x', 511) + middle + new string('y', 1030);
        using var document = JsonDocument.Parse(StandardEnvelope.Serialize(TextMessage("value", text), DeliveryId));
        Assert.Equal(JsonSerializer.Serialize(text), document.RootElement.GetProperty("values").GetProperty("value").GetRawText());
    }

    [Fact]
    public void HugeTokensAreRejectedBeforeAllocatingEncodingBuffers()
    {
        var huge = new string('x', 4 * 1024 * 1024);
        foreach (var message in new[] { TextMessage("value", huge), TextMessage(huge, "") })
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<ArgumentException>(() => StandardEnvelope.Serialize(message, DeliveryId));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
        }
    }

    [Theory]
    [InlineData(false, 'a', 32)]
    [InlineData(false, 'a', 262000)]
    [InlineData(false, '<', 262000)]
    [InlineData(false, '\ud800', 262000)]
    [InlineData(true, 'a', 262000)]
    [InlineData(true, '<', 262000)]
    [InlineData(true, '\ud800', 262000)]
    public void AcceptedAndRejectedEncodingAllocationsStayWithinReservation(bool propertyName, char character, int length)
    {
        var text = new string(character, length);
        var message = propertyName ? TextMessage(text, "") : TextMessage("value", text);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { _ = StandardEnvelope.Serialize(message, DeliveryId); }
        catch (ArgumentException) { /* 包含实际转义后超限的拒绝路径。 */ }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1, StandardEnvelope.SerializationReservationBytes(message.BudgetBytes));
    }

    private static EdgeMessage TextMessage(string name, string value) =>
        new(SourceMessageId, "simulated", "meter-001", At, TimeSource.Gateway, At,
            new Dictionary<string, FieldValue> { [name] = FieldValue.FromString(value) });

    [Fact]
    public void EnvelopeMatchesThePlatformMessageStructure()
    {
        var message = new EdgeMessage(
            SourceMessageId,
            "simulated",
            "meter-001",
            At,
            TimeSource.Gateway,
            At,
            new Dictionary<string, FieldValue>
            {
                ["voltage"] = FieldValue.FromSingle(220.5f),
                ["energy"] = FieldValue.FromUInt64(18_446_744_073_709_551_615UL),
                ["count"] = FieldValue.FromInt64(9_007_199_254_740_993L),
            },
            new Dictionary<string, QualityCode> { ["current"] = QualityCode.ReadTimeout });

        var json = Encoding.UTF8.GetString(StandardEnvelope.Serialize(message, DeliveryId));

        Assert.Equal(
            """{"schemaVersion":1,"messageId":"a4b5e193-090d-4a88-8d19-5b6b7d21f053","sourceMessageId":"071529ee-c6ad-4c56-b9b6-34b0e72e5120","deviceKey":"meter-001","collectedAt":"2026-09-28T08:30:00.123Z","timeSource":"gateway","values":{"count":9007199254740993,"energy":18446744073709551615,"voltage":220.5},"quality":{"current":{"status":"bad","reason":"readTimeout"}}}""",
            json);
    }

    [Fact]
    public void ValuesAndQualityAreAlwaysPresent()
    {
        var message = new EdgeMessage(SourceMessageId, "simulated", "meter-001", At, TimeSource.GatewayUnsynced, At, []);

        using var document = JsonDocument.Parse(StandardEnvelope.Serialize(message, DeliveryId));

        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("values").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("values").EnumerateObject());
        Assert.Empty(document.RootElement.GetProperty("quality").EnumerateObject());
        Assert.Equal("gateway-unsynced", document.RootElement.GetProperty("timeSource").GetString());
    }

    [Fact]
    public void TimesAreUtcWithThreeFractionalDigitsTruncated()
    {
        Assert.Equal("2026-09-28T08:30:00.000Z", StandardEnvelope.FormatTime(new DateTimeOffset(2026, 9, 28, 8, 30, 0, TimeSpan.Zero)));
        Assert.Equal("2026-09-28T08:30:00.999Z", StandardEnvelope.FormatTime(new DateTimeOffset(2026, 9, 28, 8, 30, 0, TimeSpan.Zero).AddTicks(9_999_999)));

        // 消息只接受 UTC；即便传入带偏移的时间，写出的也是对应的 UTC 时刻。
        Assert.Equal("2026-09-28T00:30:00.000Z", StandardEnvelope.FormatTime(new DateTimeOffset(2026, 9, 28, 8, 30, 0, TimeSpan.FromHours(8))));
    }

    [Fact]
    public void NamesMatchTheEnumJsonNames()
    {
        foreach (var timeSource in Enum.GetValues<TimeSource>())
        {
            Assert.Equal(JsonSerializer.Serialize(timeSource), $"\"{StandardEnvelope.NameOf(timeSource)}\"");
        }

        foreach (var reason in Enum.GetValues<QualityCode>())
        {
            Assert.Equal(JsonSerializer.Serialize(reason), $"\"{StandardEnvelope.NameOf(reason)}\"");
        }
    }

    [Fact]
    public void EachTargetGetsItsOwnMessageIdWhileTheSourceIdStaysTheSame()
    {
        var message = new EdgeMessage(SourceMessageId, "simulated", "meter-001", At, TimeSource.Gateway, At, []);
        var other = Guid.CreateVersion7();

        using var first = JsonDocument.Parse(StandardEnvelope.Serialize(message, DeliveryId));
        using var second = JsonDocument.Parse(StandardEnvelope.Serialize(message, other));

        Assert.Equal(DeliveryId.ToString(), first.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(other.ToString(), second.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(
            first.RootElement.GetProperty("sourceMessageId").GetString(),
            second.RootElement.GetProperty("sourceMessageId").GetString());
        Assert.Throws<ArgumentException>(() => StandardEnvelope.Serialize(message, Guid.Empty));
    }
}
