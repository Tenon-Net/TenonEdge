using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;

namespace TenonEdge.UnitTests.Inputs;

public sealed class InputDraftValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void DraftsAllowEmptyDisabledAndTenThousandValidatedPoints(int count)
    {
        var points = Enumerable.Range(0, count).Select(index => new ModbusPointSettings
        { Key = "p" + index, FunctionCode = 3, Address = index, Type = "uint16", Enabled = false }).ToArray();
        var result = InputDraftValidation.Normalize(2, Device(), new("v", "fast", " Fast ", Modbus: new(1000, points)));
        Assert.Equal(count, result.Modbus!.Points.Length);
        Assert.Equal("Fast", result.Name);
        Assert.All(result.Modbus.Points, point => Assert.False(point.Enabled));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(86400001)]
    public void InvalidCycleIsRejected(int milliseconds) =>
        Assert.Throws<ArgumentException>(() => InputDraftValidation.Normalize(2, Device(), new("v", "fast", "Fast", Modbus: new(milliseconds, []))));

    [Fact]
    public void IncompatibleDeviceDuplicateKeysAndUnknownPointPropertiesAreRejected()
    {
        var point = new ModbusPointSettings { Key = "p", FunctionCode = 3, Address = 0, Type = "uint16" };
        var request = new InputDraftRequest("v", "fast", "Fast", Modbus: new(1000, [point, point]));
        Assert.Throws<ArgumentException>(() => InputDraftValidation.Normalize(2, Device(), request));
        Assert.Throws<ArgumentException>(() => InputDraftValidation.Normalize(2, Device() with { Kind = DeviceKind.Http }, request));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<ModbusPointSettings>(
            """{"key":"p","functionCode":3,"address":0,"type":"uint16","secret":"not-real"}""", System.Text.Json.JsonSerializerOptions.Web));
    }

    private static DeviceDraft Device() => new(1, "meter", "Meter", DeviceKind.Modbus, null, 1, 1, ModbusAddressNotation.Offset, true);
}
