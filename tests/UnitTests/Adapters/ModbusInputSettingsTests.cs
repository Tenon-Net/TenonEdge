using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusInputSettingsTests
{
    private const string Valid = """
        {"connectionId":1,"host":"DEVICE.local","unitId":0,"points":[{"key":"value","functionCode":3,"address":0,"type":"uint16"}]}
        """;

    [Fact]
    public void NormalizesConnectionAndKeepsExplicitZeroUnitAndOffset()
    {
        var settings = Parse(Valid);
        Assert.Equal("device.local", settings.Host);
        Assert.Equal((byte)0, settings.UnitId);
        Assert.Equal(0, Assert.Single(settings.Points).Address);
        Assert.Equal(5000, settings.PeriodMs);
        Assert.Equal(5000, settings.ReadTimeoutMs);
    }

    [Theory]
    [InlineData("unitId", "null")]
    [InlineData("unitId", "256")]
    [InlineData("unitId", "-1")]
    [InlineData("periodMs", "999")]
    [InlineData("frameFormat", "\"rtu\"")]
    [InlineData("host", "\"http://device\"")]
    [InlineData("maxRegisters", "126")]
    [InlineData("maxBits", "2001")]
    [InlineData("unknown", "true")]
    [InlineData("points", "[]")]
    [InlineData("points", "[null]")]
    public void RejectsInvalidSettings(string key, string value)
    {
        var json = JsonNode.Parse(Valid)!;
        json[key] = JsonNode.Parse(value);
        var error = Record.Exception(() => Parse(json.ToJsonString()));
        Assert.True(error is ArgumentException or JsonException, $"Unexpected failure: {error}");
    }

    [Theory]
    [InlineData("address", "null")]
    [InlineData("address", "65536")]
    [InlineData("type", "\"bool\"")]
    [InlineData("type", "\"uint32\"")]
    [InlineData("type", "\"string\"")]
    [InlineData("byteOrder", "\"ABCD\"")]
    [InlineData("bit", "0")]
    [InlineData("functionCode", "5")]
    [InlineData("unknown", "1")]
    public void RejectsInvalidPointEvenWhenDisabled(string key, string value)
    {
        var json = JsonNode.Parse(Valid)!;
        json["points"]![0]![key] = JsonNode.Parse(value);
        json["points"]![0]!["enabled"] = false;
        json["points"]!.AsArray().Add(JsonNode.Parse("""
            {"key":"valid","functionCode":3,"address":5,"type":"uint16"}
            """));
        var error = Record.Exception(() => Parse(json.ToJsonString()));
        Assert.True(error is ArgumentException or JsonException, $"Unexpected failure: {error}");
    }

    [Theory]
    [InlineData("tcp", 0, true)]
    [InlineData("tcp", 255, true)]
    [InlineData("rtu-over-tcp", 1, true)]
    [InlineData("rtu-over-tcp", 247, true)]
    [InlineData("rtu-over-tcp", 0, false)]
    [InlineData("rtu-over-tcp", 248, false)]
    public void ValidatesFrameSpecificUnitRange(string format, int unit, bool accepted)
    {
        var json = JsonNode.Parse(Valid)!;
        json["frameFormat"] = format;
        json["unitId"] = unit;
        if (accepted)
        { Assert.Equal((byte)unit, Parse(json.ToJsonString()).UnitId); }
        else
        { Assert.Throws<ArgumentException>(() => Parse(json.ToJsonString())); }
    }

    [Fact]
    public void RequiresUnitAndAddressRatherThanSilentlyDefaultingToZero()
    {
        var json = JsonNode.Parse(Valid)!.AsObject();
        json.Remove("unitId");
        Assert.Throws<ArgumentException>(() => Parse(json.ToJsonString()));
        json["unitId"] = 0;
        json["points"]![0]!.AsObject().Remove("address");
        Assert.Throws<ArgumentException>(() => Parse(json.ToJsonString()));
    }

    private static ModbusInputSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ModbusInputSettings.Parse(document.RootElement);
    }
}
