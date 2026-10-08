using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

public sealed class RawJsonPayloadTests
{
    [Fact]
    public void CopyOwnsItsDocumentAfterTheProtocolDocumentIsDisposed()
    {
        RawJsonPayload payload;
        const string json = "{\"nested\":{\"n\":9223372036854775807},\"private\":\"unselected-secret\"}";
        using (var document = JsonDocument.Parse(json))
        { payload = RawJsonPayload.CopyFrom(document.RootElement); }
        Assert.Equal(long.MaxValue, payload.Root.GetProperty("nested").GetProperty("n").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(json), payload.ByteLength);
        Assert.Equal(16L * payload.ByteLength + 1024, payload.BudgetBytes);
        Assert.DoesNotContain("unselected-secret", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unselected-secret", JsonSerializer.Serialize(payload), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a", 1048574, true)]
    [InlineData("a", 1048575, false)]
    [InlineData("汉", 349524, true)]
    [InlineData("汉", 349525, false)]
    public void LimitCountsUtf8BytesNotCharacters(string character, int length, bool allowed)
    {
        using var document = JsonDocument.Parse("\"" + new string(character[0], length) + "\"");
        if (allowed)
        {
            var payload = RawJsonPayload.CopyFrom(document.RootElement);
            Assert.InRange(payload.ByteLength, 1, RawJsonPayload.MaximumBytes);
            Assert.InRange(payload.BudgetBytes, 1, RawJsonPayload.MaximumBudgetBytes);
        }
        else
        { Assert.Throws<ArgumentException>(() => RawJsonPayload.CopyFrom(document.RootElement)); }
    }

    [Fact]
    public void MessageChargesRawOwnershipButDefaultOutputsNeverSerializeIt()
    {
        using var document = JsonDocument.Parse("""{"private":"unselected-secret"}""");
        var payload = RawJsonPayload.CopyFrom(document.RootElement);
        var at = DateTimeOffset.UnixEpoch;
        var values = new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) };
        var without = new EdgeMessage(Guid.NewGuid(), "http", "device", at, TimeSource.Gateway, at, values);
        var withRaw = new EdgeMessage(without.SourceMessageId, "http", "device", at, TimeSource.Gateway, at, values, rawJson: payload);
        Assert.Equal(payload.BudgetBytes, withRaw.BudgetBytes - without.BudgetBytes);
        var delivery = Guid.NewGuid();
        Assert.Equal(StandardEnvelope.Serialize(without, delivery).ToArray(), StandardEnvelope.Serialize(withRaw, delivery).ToArray());
        Assert.DoesNotContain("unselected-secret", JsonSerializer.Serialize(withRaw), StringComparison.Ordinal);
    }
}
