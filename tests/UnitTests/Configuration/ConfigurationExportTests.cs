using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Configuration;

public sealed class ConfigurationExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static ConfigurationCheckpoint Draft() => new("private-draft-version",
        [new(101, "receiver", "Receiver", ConnectionKind.Http, true, null, null, new("https://example.com/base/"))],
        [new(202, "meter", "Meter", DeviceKind.Simulator, "floor", null, null, ModbusAddressNotation.Offset, true)],
        [new(303, 202, "values", "Values", DeviceKind.Simulator, false, null, Simulator: new(SampleJson: "{\"value\":9223372036854775807}"))],
        [new(404, "route", "Route", "Unfinished routes may be exported", false,
            new(1, [new("source", "input.simulator", 1, "Source", Json("""{"sourceIds":[303]}""")),
                new("target", "output.http", 1, "Target", Json("""{"connectionId":101,"path":"/data","payload":{"mode":"custom","fields":[{"target":"id","constant":9223372036854775807}]}}"""))],
                [new("source", "target")], new(new() { ["source"] = new(1, 2) })), "private-editor", 123456)],
        [101], [new("connection", 101, "private-edit-stamp", "private-editor", 123456, false)]);

    [Fact]
    public void PortableFileUsesOnlyBusinessReferencesAndKeepsNodeIdsLayoutDisabledObjectsAndExactNumbers()
    {
        var draft = Draft();
        var portable = ConfigurationExport.Create(draft, new Dictionary<long, string> { [101] = "sha256:" + new string('a', 64) }, Token);
        var bytes = ConfigurationExport.Serialize(portable, Token);
        using var parsed = JsonDocument.Parse(bytes);
        var root = parsed.RootElement;
        Assert.Equal(["formatVersion", "connections", "devices", "inputs", "routes"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("receiver", portable.Connections[0].ConnectionKey);
        Assert.Equal("requiresInput", portable.Connections[0].Credential!.Status);
        Assert.Equal("meter", portable.Devices[0].DeviceKey);
        Assert.Equal("meter", portable.Inputs[0].DeviceKey);
        Assert.Equal("values", portable.Inputs[0].InputKey);
        Assert.False(portable.Inputs[0].Enabled);
        Assert.False(portable.Routes[0].Enabled);
        Assert.Equal("meter/values", portable.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceKeys")[0].GetString());
        var output = portable.Routes[0].Graph.Nodes[1].Config;
        Assert.Equal("receiver", output.GetProperty("connectionKey").GetString());
        Assert.Equal("9223372036854775807", output.GetProperty("payload").GetProperty("fields")[0].GetProperty("constant").GetRawText());
        Assert.Equal(draft.Inputs[0].Simulator!.SampleJson, portable.Inputs[0].Simulator!.SampleJson);
        Assert.Equal(new RouteNodePosition(1, 2), portable.Routes[0].Graph.Layout.Nodes["source"]);
        Assert.Equal("source", portable.Routes[0].Graph.Nodes[0].Id);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var excluded in new[] { "private-draft-version", "private-editor", "private-edit-stamp", "connectionId", "sourceIds", "deviceId", "credentialConnections", "updatedAt", "updatedBy" })
        { Assert.DoesNotContain(excluded, text, StringComparison.Ordinal); }
        Assert.Equal(303, draft.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceIds")[0].GetInt64());
        Assert.Equal(101, draft.Routes[0].Graph.Nodes[1].Config.GetProperty("connectionId").GetInt64());
    }

    [Theory]
    [InlineData("device")]
    [InlineData("input")]
    [InlineData("output")]
    public void UnresolvableResourceReferencesRejectTheWholeFile(string missing)
    {
        var draft = Draft();
        draft = missing switch
        {
            "device" => draft with { Devices = [] },
            "input" => draft with { Inputs = [] },
            _ => draft with { Connections = [] },
        };
        var error = Assert.Throws<ConfigurationTransferException>(() => ConfigurationExport.Create(draft, new Dictionary<long, string>(), Token));
        Assert.Equal("configuration.exportReferenceMissing", error.Code);
        Assert.StartsWith("$.", error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializationEnforcesExactByteBoundaryAndDoesNotReturnAPartialFile()
    {
        var value = ConfigurationExport.Create(Draft(), new Dictionary<long, string>(), Token);
        var expected = ConfigurationExport.Serialize(value, Token);
        Assert.Equal(expected, ConfigurationExport.Serialize(value, Token, expected.Length));
        var error = Assert.Throws<ConfigurationTransferException>(() => ConfigurationExport.Serialize(value, Token, expected.Length - 1));
        Assert.Equal("configuration.exportTooLarge", error.Code);
    }

    [Fact]
    public void UnselectedOutputAndEmptyInputRemainUnfinishedInsteadOfInventingReferences()
    {
        var draft = Draft();
        draft.Routes[0].Graph.Nodes[0] = draft.Routes[0].Graph.Nodes[0] with { Config = Json("""{"sourceIds":[]}""") };
        draft.Routes[0].Graph.Nodes[1] = draft.Routes[0].Graph.Nodes[1] with { Config = Json("""{"connectionId":null}""") };
        var result = ConfigurationExport.Create(draft, new Dictionary<long, string>(), Token);
        Assert.Empty(result.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceKeys").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.Routes[0].Graph.Nodes[1].Config.GetProperty("connectionKey").ValueKind);
        Assert.Null(result.Connections[0].Credential);
    }

    [Fact]
    public void AllSupportedConnectionAndSourceSettingsSurviveExportWithPortableDeviceReferences()
    {
        var draft = Draft();
        draft = draft with
        {
            Connections = [.. draft.Connections,
                new(102, "plc", "PLC", ConnectionKind.Modbus, false, new("plc.example.com", Format: ModbusFrameFormat.RtuOverTcp), null, null),
                new(103, "broker", "Broker", ConnectionKind.Mqtt, true, null, new("broker.example.com", 8883, true, "edge-client"), null)],
            Devices = [.. draft.Devices,
                new(203, "modbus", "Modbus", DeviceKind.Modbus, null, 102, 247, ModbusAddressNotation.Manual, false),
                new(204, "mqtt", "MQTT", DeviceKind.Mqtt, null, 103, null, ModbusAddressNotation.Offset, true),
                new(205, "http", "HTTP", DeviceKind.Http, null, 101, null, ModbusAddressNotation.Offset, true)],
            Inputs = [.. draft.Inputs,
                new(304, 203, "fast", "Fast", DeviceKind.Modbus, true, new(1234,
                    [new() { Key = "temperature", Name = "Temperature", FunctionCode = 3, Address = 17, Type = "float32", ByteOrder = "CDAB", Unit = "C", Enabled = false }])),
                new(305, 204, "telemetry", "Telemetry", DeviceKind.Mqtt, true, null, Mqtt: new("meters/+/data",
                    [new("power", "/watts", FieldType.Number, "Power", "W")], Qos: 1, ProcessRetained: true,
                    UpstreamTime: new("/time", UpstreamTimeFormat.UnixMilliseconds))),
                new(306, 205, "poll", "Poll", DeviceKind.Http, true, null, Http: new("status?channel=1",
                    [new("count", "/count", FieldType.Integer)], 2345, new() { ["X-Device"] = "meter" }))],
        };
        var exported = ConfigurationExport.Create(draft, new Dictionary<long, string>(), Token);
        var restored = JsonSerializer.Deserialize<PortableConfiguration>(ConfigurationExport.Serialize(exported, Token), ConfigurationExport.Json)!;
        Assert.Equal(draft.Connections[1].Modbus, restored.Connections[1].Modbus);
        Assert.Equal(draft.Connections[2].Mqtt, restored.Connections[2].Mqtt);
        Assert.Equal("plc", restored.Devices[1].ConnectionKey);
        Assert.Equal(247, restored.Devices[1].UnitId);
        Assert.Equal(ModbusAddressNotation.Manual, restored.Devices[1].AddressNotation);
        Assert.False(restored.Devices[1].Enabled);
        Assert.Equal("broker", restored.Devices[2].ConnectionKey);
        Assert.Equal("receiver", restored.Devices[3].ConnectionKey);
        for (var i = 0; i < draft.Inputs.Length; i++)
        {
            Assert.Equal(JsonSerializer.Serialize(draft.Inputs[i].Modbus), JsonSerializer.Serialize(restored.Inputs[i].Modbus));
            Assert.Equal(JsonSerializer.Serialize(draft.Inputs[i].Mqtt), JsonSerializer.Serialize(restored.Inputs[i].Mqtt));
            Assert.Equal(JsonSerializer.Serialize(draft.Inputs[i].Http), JsonSerializer.Serialize(restored.Inputs[i].Http));
        }
    }

    [Fact]
    public void SecretBypassInConnectionMetadataIsRejectedEvenForAnUnreferencedConnection()
    {
        var draft = Draft();
        draft.Connections[0] = draft.Connections[0] with { Http = new("https://example.com/", Headers: new() { ["Authorization"] = "Bearer not-a-real-secret" }) };
        Assert.Throws<ArgumentException>(() => ConfigurationExport.Create(draft, new Dictionary<long, string>(), Token));
    }

    [Fact]
    public void CancelledExportDoesNotProduceAFile()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ConfigurationExport.Create(Draft(), new Dictionary<long, string>(), stop.Token));
        Assert.Throws<OperationCanceledException>(() => ConfigurationExport.Serialize(new(1, [], [], [], []), stop.Token));
    }
}
