using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Routes;

public sealed class RoutePreviewSampleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Time = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static RouteCompilationSnapshot Snapshot(DeviceKind kind)
    {
        JsonSourceField[] fields = [new("value", "/data/value", FieldType.Integer), new("precise", "/data/precise", FieldType.Number)];
        var time = new SourceTimeMapping("/timestamp", UpstreamTimeFormat.UnixSeconds);
        var connection = kind switch
        {
            DeviceKind.Http => new ConnectionDraft(10, "http", "HTTP", ConnectionKind.Http, true, null, null, new("http://localhost/")),
            DeviceKind.Mqtt => new ConnectionDraft(10, "mqtt", "MQTT", ConnectionKind.Mqtt, true, null, new("localhost"), null),
            DeviceKind.Modbus => new ConnectionDraft(10, "modbus", "Modbus", ConnectionKind.Modbus, true, new("localhost"), null, null),
            _ => null,
        };
        var input = new InputDraft(1, 1, "values", "Values", kind, true,
            kind == DeviceKind.Modbus ? new(5000, [new ModbusPointSettings { Key = "value", FunctionCode = 3, Address = 0, Type = "uint64", ByteOrder = "ABCD" }]) : null,
            kind == DeviceKind.Mqtt ? new("sample/#", fields, UpstreamTime: time) : null,
            kind == DeviceKind.Http ? new("sample", fields, UpstreamTime: time) : null,
            kind == DeviceKind.Simulator ? new(SampleJson: "{\"value\":1}") : null);
        return new("version", [new(1, "route", "Route", null, false, new(1, [], [], new([])), null, 0)],
            new Dictionary<long, DeviceDraft>
            {
                [1] = new(1, "device", "Device", kind, null, connection?.Id,
                kind == DeviceKind.Modbus ? 1 : null, ModbusAddressNotation.Offset, true)
            },
            new Dictionary<long, InputDraft> { [1] = input },
            connection is null ? new Dictionary<long, ConnectionDraft>() : new Dictionary<long, ConnectionDraft> { [10] = connection }, new HashSet<long>());
    }
    private static RouteDecodedSample Decode(DeviceKind kind, string json) =>
        RoutePreviewSample.Decode(Snapshot(kind), 1, json, Time, ClockSyncState.NotSynchronized, Token);

    [Theory]
    [InlineData("Http")]
    [InlineData("Mqtt")]
    public void JsonProtocolsUseRealPointersFixedTypesTimeAndRawPayloadWithoutRounding(string kindName)
    {
        var kind = Enum.Parse<DeviceKind>(kindName);
        var result = Decode(kind, """{"data":{"value":9223372036854775807,"precise":0.1234567890123456789012345678},"timestamp":1700000000}""");
        Assert.Equal("9223372036854775807", result.Message.Values["value"].ToString());
        Assert.Equal("0.1234567890123456789012345678", result.Message.Values["precise"].ToString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.Message.CollectedAt);
        Assert.Equal(TimeSource.Upstream, result.Message.TimeSource);
        Assert.Equal(Time, result.Message.ReceivedAt);
        Assert.NotNull(result.Message.RawJson);
        Assert.False(result.InvalidUpstreamTime);
        var invalid = Decode(kind, """{"data":{"value":"not a number"},"timestamp":"invalid"}""");
        Assert.True(invalid.InvalidUpstreamTime);
        Assert.Equal(Time, invalid.Message.CollectedAt);
        Assert.Equal(TimeSource.GatewayUnsynced, invalid.Message.TimeSource);
        Assert.Equal(QualityCode.ConversionFailed, invalid.Message.Quality["value"]);
        Assert.False(invalid.Message.Values.ContainsKey("precise"));
    }

    [Theory]
    [InlineData("18446744073709551615")]
    [InlineData("\"18446744073709551615\"")]
    public void DecodedModbusUnsignedFieldsKeepAllBitsAndDoNotCreateAnArtificialRawJsonPayload(string value)
    {
        var result = Decode(DeviceKind.Modbus, "{\"value\":" + value + "}");
        Assert.Equal("18446744073709551615", result.Message.Values["value"].ToString());
        Assert.Null(result.Message.RawJson);
        Assert.Equal(QualityCode.Overflow, Decode(DeviceKind.Modbus, "{\"value\":-1}").Message.Quality["value"]);
        Assert.Equal(QualityCode.Overflow, Decode(DeviceKind.Modbus, "{\"value\":18446744073709551616}").Message.Quality["value"]);
    }

    [Fact]
    public void SimulatorUsesDeclaredFieldsWithoutFaultInjectionOrRawJsonAndAmbiguousKeysRemainMissing()
    {
        var result = Decode(DeviceKind.Simulator, "{\"value\":9223372036854775807,\"unknown\":42}");
        Assert.Equal("9223372036854775807", Assert.Single(result.Message.Values).Value.ToString());
        Assert.Null(result.Message.RawJson);
        Assert.Empty(Decode(DeviceKind.Simulator, "{\"value\":1,\"value\":2}").Message.Values);
    }

    [Fact]
    public void SampleSizeDepthShapeIdentityAndCancellationAreBoundedBeforeExecution()
    {
        Assert.Throws<ArgumentException>(() => Decode(DeviceKind.Simulator, "[]"));
        Assert.Throws<ArgumentException>(() => Decode(DeviceKind.Simulator, """{"value":"\ud800"}"""));
        Assert.Throws<ArgumentException>(() => Decode(DeviceKind.Simulator, "{\"value\":\"" + new string('界', 22000) + "\"}"));
        Assert.ThrowsAny<JsonException>(() => Decode(DeviceKind.Http, new string('[', 65) + "0" + new string(']', 65)));
        Assert.Throws<RouteTopologyException>(() => RoutePreviewSample.Decode(Snapshot(DeviceKind.Simulator), 99, "{}", Time, ClockSyncState.Unknown, Token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoutePreviewSample.Decode(Snapshot(DeviceKind.Simulator), 1, "{}", Time, ClockSyncState.Unknown, cancellation.Token));
    }
}
