using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.UnitTests.Runtime;

public sealed class ModbusPlanDocumentTests
{
    private static JsonObject Input(string key = "device/fast", long connection = 1, int unit = 1, string point = "value") =>
        JsonSerializer.SerializeToNode(new
        {
            kind = "input.modbus",
            key,
            configuration = new
            {
                connectionId = connection,
                host = "DEVICE.local",
                unitId = unit,
                points = new[] { new { key = point, functionCode = 3, address = 0, type = "uint16" } }
            }
        })!.AsObject();
    private static RuntimePlanDocument Parse(params JsonObject[] units) => RuntimePlanDocument.Parse(
        JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units }));

    [Fact]
    public void ValidGroupsAndDifferentUnitsShareOneConnection()
    {
        var document = Parse(Input(), Input("device/slow", point: "other"), Input("sibling/fast", unit: 2));
        Assert.Equal(3, document.Units.Count);
        Assert.All(document.Units, unit => Assert.Equal("input.modbus", unit.Id.Kind));
    }

    [Theory]
    [InlineData("host", "\"other.local\"")]
    [InlineData("port", "1502")]
    [InlineData("readTimeoutMs", "1000")]
    [InlineData("requestIntervalMs", "100")]
    [InlineData("frameFormat", "\"rtu-over-tcp\"")]
    public void RejectsConflictingPhysicalSettingsBeforeSnapshotFreeze(string property, string value)
    {
        var second = Input("sibling/fast", unit: 2);
        second["configuration"]![property] = JsonNode.Parse(value);
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input(), second));
    }

    [Theory]
    [InlineData(1, 2, "other")]
    [InlineData(2, 1, "other")]
    [InlineData(1, 1, "value")]
    public void RejectsDeviceIdentityOrPointKeyConflicts(long connection, int unit, string point) =>
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input(), Input("device/slow", connection, unit, point)));

    [Theory]
    [InlineData("input.http", """{"connectionId":1,"baseAddress":"http://device.local/","fields":[{"key":"value","path":"/v","type":"integer"}]}""")]
    [InlineData("output.http", """{"connectionId":1,"baseAddress":"http://device.local/","inputs":["device/fast"]}""")]
    [InlineData("output.mqtt", """{"connectionId":1,"host":"device.local","topic":"events","inputs":["device/fast"]}""")]
    public void RejectsCrossProtocolReuseOfConnectionId(string kind, string configuration)
    {
        var other = new JsonObject { ["kind"] = kind, ["key"] = kind.StartsWith("input.", StringComparison.Ordinal) ? "other/fast" : "out", ["configuration"] = JsonNode.Parse(configuration) };
        Assert.Single(Parse(other).Units);
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input(), other));
    }

    [Fact]
    public void EnforcesModbusConnectionCountNotInputGroupCount()
    {
        var maximum = Enumerable.Range(1, 256).Select(index => Input($"device{index}/fast", index)).ToArray();
        Assert.Equal(256, Parse(maximum).Units.Count);
        Assert.Throws<RuntimePlanDocumentException>(() => Parse([.. maximum, Input("extra/fast", 257)]));
    }

    [Fact]
    public void InvalidConfigurationDiagnosticsNeverEchoExternalContent()
    {
        var input = Input();
        input["configuration"]!["MARKER-secret"] = "sensitive-external-content";
        var error = Assert.Throws<RuntimePlanDocumentException>(() => Parse(input));
        Assert.DoesNotContain("MARKER", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.Ordinal);
        Assert.Throws<RuntimePlanDocumentException>(() => Parse(Input("not-a-device-input")));
    }
}
