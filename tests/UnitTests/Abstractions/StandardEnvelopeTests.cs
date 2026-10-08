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
