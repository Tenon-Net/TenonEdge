using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.UnitTests.Devices;

public sealed class DeviceDraftValidationTests
{
    [Theory]
    [InlineData(ModbusFrameFormat.Tcp, 0, true)]
    [InlineData(ModbusFrameFormat.Tcp, 255, true)]
    [InlineData(ModbusFrameFormat.Tcp, 256, false)]
    [InlineData(ModbusFrameFormat.Tcp, -1, false)]
    [InlineData(ModbusFrameFormat.RtuOverTcp, 0, false)]
    [InlineData(ModbusFrameFormat.RtuOverTcp, 1, true)]
    [InlineData(ModbusFrameFormat.RtuOverTcp, 247, true)]
    [InlineData(ModbusFrameFormat.RtuOverTcp, 248, false)]
    public void UnitIdUsesReferencedConnectionFrameFormat(ModbusFrameFormat format, int unitId, bool valid)
    {
        var connection = ConnectionDraftValidation.Normalize(1, new("v", "plc", "PLC", ConnectionKind.Modbus,
            Modbus: new("10.0.0.1", Format: format)));
        var request = new DeviceDraftRequest("v", "meter", " Meter ", DeviceKind.Modbus, "  line one  ", 1, unitId,
            ModbusAddressNotation.Manual);
        if (!valid)
        { Assert.Throws<ArgumentException>(() => DeviceDraftValidation.Normalize(2, request, connection)); return; }
        var device = DeviceDraftValidation.Normalize(2, request, connection);
        Assert.Equal("Meter", device.Name);
        Assert.Equal("line one", device.Group);
        Assert.Equal(unitId, device.UnitId);
        Assert.Equal(ModbusAddressNotation.Manual, device.AddressNotation);
    }

    [Theory]
    [InlineData((int)DeviceKind.Modbus)]
    [InlineData((int)DeviceKind.Mqtt)]
    [InlineData((int)DeviceKind.Http)]
    public void PhysicalDevicesRequireAnExistingMatchingConnection(int value)
    {
        var kind = (DeviceKind)value;
        var request = new DeviceDraftRequest("v", "meter", "Meter", kind, ConnectionId: 1, UnitId: kind == DeviceKind.Modbus ? 1 : null);
        Assert.Throws<ArgumentException>(() => DeviceDraftValidation.Normalize(2, request, null));
        var wrong = ConnectionDraftValidation.Normalize(1, new("v", "conn", "Connection",
            kind == DeviceKind.Mqtt ? ConnectionKind.Http : ConnectionKind.Mqtt,
            Mqtt: kind == DeviceKind.Mqtt ? null : new("10.0.0.1"),
            Http: kind == DeviceKind.Mqtt ? new("http://10.0.0.1/") : null));
        Assert.Throws<ArgumentException>(() => DeviceDraftValidation.Normalize(2, request, wrong));
    }

    [Fact]
    public void SimulatorRejectsPhysicalSettingsAndInvalidIdentifiers()
    {
        var request = new DeviceDraftRequest("v", "meter", "Meter", DeviceKind.Simulator);
        Assert.Null(DeviceDraftValidation.Normalize(2, request, null).ConnectionId);
        DeviceDraftRequest[] invalid =
        [
            request with { ConnectionId = 1 }, request with { UnitId = 1 },
            request with { AddressNotation = ModbusAddressNotation.Manual },
            request with { AddressNotation = (ModbusAddressNotation)5 }, request with { Kind = (DeviceKind)5 },
            request with { Key = "bad/key" }, request with { Name = " " }, request with { Name = "bad\nname" },
            request with { Name = new string('x', 65) }, request with { Group = new string('x', 65) },
            request with { Group = "bad\ngroup" }, request with { ExpectedVersion = "" },
        ];
        foreach (var value in invalid)
        { Assert.Throws<ArgumentException>(() => DeviceDraftValidation.Normalize(2, value, null)); }
    }
}
