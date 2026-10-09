using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Devices;

public sealed class DeviceReferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReferencesAndGlobalVersionAreCheckedTogetherAndFrameChangesCannotInvalidateDevices()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var connection = connections.Write(null, new("0", "plc", "PLC", ConnectionKind.Modbus, Modbus: new("10.0.0.1")), "fixture", null);
        var device = devices.Write(null, new(connection.Version, "meter", "Meter", DeviceKind.Modbus,
            ConnectionId: connection.Connection.Id, UnitId: 0), "fixture", null);
        Assert.Equal(device.Version, connections.Read().Version);
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() =>
            connections.Delete(connection.Connection.Id, connection.Version, "fixture", null)).Code);
        Assert.Equal("connection.deviceReferenced", Assert.Throws<DraftWriteException>(() =>
            connections.Delete(connection.Connection.Id, device.Version, "fixture", null)).Code);
        Assert.Equal("connection.deviceConflict", Assert.Throws<DraftWriteException>(() =>
            connections.Write(connection.Connection.Id, new(device.Version, "plc", "PLC", ConnectionKind.Modbus,
                Modbus: new("10.0.0.1", Format: ModbusFrameFormat.RtuOverTcp)), "fixture", null)).Code);
        Assert.Equal(device.Version, devices.Read().Version);
        var changed = devices.Write(device.Device.Id, new(device.Version, "meter", "Meter", DeviceKind.Modbus,
            ConnectionId: connection.Connection.Id, UnitId: 1), "fixture", null);
        var rtu = connections.Write(connection.Connection.Id, new(changed.Version, "plc", "PLC", ConnectionKind.Modbus,
            Modbus: new("10.0.0.1", Format: ModbusFrameFormat.RtuOverTcp)), "fixture", null);
        var deleted = devices.Delete(device.Device.Id, rtu.Version, "fixture", null);
        connections.Delete(connection.Connection.Id, deleted.Version, "fixture", null);
        Assert.Empty(connections.Read().Connections);
    }

    [Fact]
    public async Task MissingConnectionOrWrongProtocolDoesNotChangeTheDatabase()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var failed = await admin.PostJsonAsync("/api/edge-admin/v1/devices/",
            new DeviceDraftRequest("0", "meter", "Meter", DeviceKind.Modbus, ConnectionId: 999, UnitId: 1), Token);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var store = factory.Services.GetRequiredService<DeviceDraftStore>();
        Assert.Equal("0", store.Read().Version);
        Assert.Empty(store.Read().Devices);
        Assert.Empty(TestAccounts.Audit(factory.Services, "device.draftCreated"));
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("previous")]
    [InlineData("pending")]
    public async Task RetainedSeedKeysCannotBeClaimedOrDeleted(string referenceKind)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var created = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/input","configuration":{"periodMs":5000}}]}"""));
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(db =>
        {
            db.Insertable(new RuntimeSnapshotRecord
            {
                Revision = 1,
                SchemaVersion = 1,
                Content = plan.CanonicalText,
                ContentHash = plan.ContentHash,
                Origin = RuntimeSnapshotOrigins.Seed,
                State = RuntimeSnapshotStates.Effective,
                CreatedBy = "fixture",
            }).ExecuteCommand();
            var state = db.Queryable<SystemStateRecord>().InSingle(1);
            if (referenceKind == "effective")
            { state.EffectiveRevision = 1; }
            if (referenceKind == "previous")
            { state.PreviousRevision = 1; }
            if (referenceKind == "pending")
            { state.PendingRevision = 1; }
            db.Updateable(state).ExecuteCommand();
            return true;
        });
        Assert.Equal("device.referenced", Assert.Throws<DraftWriteException>(() =>
            devices.Delete(created.Device.Id, created.Version, "fixture", null)).Code);
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(db =>
        {
            db.Deleteable<DeviceDraftRecord>().Where(row => row.Id == created.Device.Id).ExecuteCommand();
            return true;
        });
        Assert.Equal("device.reservedKey", Assert.Throws<DraftWriteException>(() =>
            devices.Write(null, new(created.Version, "meter", "Meter", DeviceKind.Simulator), "fixture", null)).Code);
        Assert.Equal(created.Version, devices.Read().Version);
    }

    [Theory]
    [InlineData("device.draftUpdated")]
    [InlineData("device.draftDeleted")]
    public async Task AuditFailureRollsBackUpdatesAndDeletes(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var created = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_device_audit BEFORE INSERT ON audit_log WHEN NEW.action = '{action}' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        Assert.ThrowsAny<Exception>(() =>
        {
            if (action == "device.draftUpdated")
            { devices.Write(created.Device.Id, new(created.Version, "meter", "Changed", DeviceKind.Simulator), "fixture", null); }
            else
            { devices.Delete(created.Device.Id, created.Version, "fixture", null); }
        });
        var current = devices.Read();
        Assert.Equal(created.Version, current.Version);
        Assert.Equal("Meter", Assert.Single(current.Devices).Name);
    }
}
