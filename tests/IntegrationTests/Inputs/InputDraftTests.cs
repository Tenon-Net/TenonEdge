using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class InputDraftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RolesReadAndOnlyEditorsWrite(string role, HttpStatusCode status)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var read = await client.GetAsync(Path(device.Device.Id), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await client.PostJsonAsync(Path(device.Device.Id), Request(device.Version), Token);
        Assert.Equal(status, write.StatusCode);
        if (status == HttpStatusCode.Forbidden)
        {
            var store = factory.Services.GetRequiredService<InputDraftStore>();
            var created = store.Write(device.Device.Id, null, Request(device.Version), "fixture", null);
            using var update = await client.PutAsJsonAsync(Path(device.Device.Id) + created.Input.Id, Request(created.Version), Token);
            using var delete = await client.DeleteAsync(Path(device.Device.Id) + created.Input.Id + "?expectedVersion=" + created.Version, Token);
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousAndCsrfCannotWrite()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        using var anonymous = factory.CreateClient();
        using var read = await anonymous.GetAsync(Path(device.Device.Id), Token);
        using var denied = await anonymous.PostJsonAsync(Path(device.Device.Id), Request(device.Version), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Path(device.Device.Id), Request(device.Version), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<InputDraftStore>().Read(device.Device.Id).Inputs);
    }

    [Fact]
    public async Task FieldsAreUniqueAcrossSourcesAndOwnershipAndVersionAreAtomic()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var created = store.Write(device.Device.Id, null, Request(device.Version), "fixture", null);
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() =>
            store.Write(device.Device.Id, null, Request(device.Version) with { Key = "slow" }, "fixture", null)).Code);
        Assert.Equal("input.duplicateDeviceField", Assert.Throws<DraftWriteException>(() =>
            store.Write(device.Device.Id, null, Request(created.Version) with { Key = "slow" }, "fixture", null)).Code);
        Assert.Equal("input.immutableKey", Assert.Throws<DraftWriteException>(() =>
            store.Write(device.Device.Id, created.Input.Id, Request(created.Version) with { Key = "renamed" }, "fixture", null)).Code);
        var other = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(created.Version, "other", "Other", DeviceKind.Modbus, ConnectionId: device.Device.ConnectionId, UnitId: 2), "fixture", null);
        Assert.Equal("input.notFound", Assert.Throws<DraftWriteException>(() =>
            store.Write(other.Device.Id, created.Input.Id, Request(other.Version), "fixture", null)).Code);
        Assert.Equal("input.notFound", Assert.Throws<DraftWriteException>(() =>
            store.Delete(other.Device.Id, created.Input.Id, other.Version, "fixture", null)).Code);
        var independent = store.Write(other.Device.Id, null, Request(other.Version), "fixture", null);
        Assert.Equal("temperature", Assert.Single(independent.Input.Modbus!.Points).Key);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        Assert.Equal("device.sourceConflict", Assert.Throws<DraftWriteException>(() =>
            devices.Write(device.Device.Id, new(independent.Version, "meter", "Meter", DeviceKind.Simulator), "fixture", null)).Code);
        devices.Delete(device.Device.Id, independent.Version, "fixture", null);
        Assert.Single(store.Read(other.Device.Id).Inputs);
    }

    [Theory]
    [InlineData("input.draftCreated")]
    [InlineData("input.draftUpdated")]
    [InlineData("input.draftDeleted")]
    public async Task FailedAuditRollsBackPointsAndVersion(string action)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var created = action == "input.draftCreated" ? null : store.Write(device.Device.Id, null, Request(device.Version), "fixture", null);
        var version = created?.Version ?? device.Version;
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_input_audit BEFORE INSERT ON audit_log WHEN NEW.action = '{action}' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        Assert.ThrowsAny<Exception>(() =>
        {
            if (action == "input.draftDeleted")
            { store.Delete(device.Device.Id, created!.Input.Id, version, "fixture", null); }
            else
            { store.Write(device.Device.Id, created?.Input.Id, Request(version) with { Name = "Changed" }, "fixture", null); }
        });
        var current = store.Read(device.Device.Id);
        Assert.Equal(version, current.Version);
        if (created is null)
        { Assert.Empty(current.Inputs); }
        else
        { Assert.Equal("Fast", Assert.Single(current.Inputs).Name); }
    }

    [Fact]
    public async Task EmptyAndDisabledPointTablesPersistButInvalidPointsDoNot()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var created = store.Write(device.Device.Id, null, Request(device.Version) with { Modbus = new(1000, []) }, "fixture", null);
        Assert.Empty(created.Input.Modbus!.Points);
        var disabled = Request(created.Version) with { Modbus = new(1000, [Point() with { Enabled = false }]) };
        var saved = store.Write(device.Device.Id, created.Input.Id, disabled, "fixture", null);
        Assert.False(Assert.Single(saved.Input.Modbus!.Points).Enabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Write(device.Device.Id, created.Input.Id,
            Request(saved.Version) with { Modbus = new(1000, [Point() with { Address = 65535, Type = "uint32", ByteOrder = "ABCD" }]) }, "fixture", null));
        Assert.Equal(saved.Version, store.Read(device.Device.Id).Version);
    }

    [Fact]
    public async Task PointTableSurvivesHostRecreationWithoutRuntimeActivation()
    {
        using var directory = TemporaryDirectory.Create();
        long deviceId;
        InputDraftResult created;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            var device = Device(factory);
            deviceId = device.Device.Id;
            created = factory.Services.GetRequiredService<InputDraftStore>().Write(deviceId, null, Request(device.Version), "fixture", null);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        var snapshot = restarted.Services.GetRequiredService<InputDraftStore>().Read(deviceId);
        var input = Assert.Single(snapshot.Inputs);
        Assert.Equal(created.Version, snapshot.Version);
        Assert.Equal(created.Input.Id, input.Id);
        Assert.Equal(0, Assert.Single(input.Modbus!.Points).Address);
        var state = restarted.Services.GetRequiredService<ConfigDatabase>().Client
            .Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PreviousRevision);
        Assert.Null(state.PendingRevision);
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("previous")]
    [InlineData("pending")]
    public async Task RetainedSnapshotReferencesPreventSourceDeletion(string referenceKind)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var created = store.Write(device.Device.Id, null, Request(device.Version), "fixture", null);
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/fast","configuration":{"periodMs":5000}}]}"""));
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(db =>
        {
            db.Insertable(new RuntimeSnapshotRecord
            {
                Revision = 1,
                SchemaVersion = 1,
                Content = plan.CanonicalText,
                ContentHash = plan.ContentHash,
                Origin = "seed",
                State = "effective",
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
        Assert.Equal("input.referenced", Assert.Throws<DraftWriteException>(() =>
            store.Delete(device.Device.Id, created.Input.Id, created.Version, "fixture", null)).Code);
        Assert.Equal(created.Version, store.Read(device.Device.Id).Version);
    }

    [Fact]
    public async Task FailedDeviceDeletionRestoresOwnedSourcesInTheSameTransaction()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = Device(factory);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var created = store.Write(device.Device.Id, null, Request(device.Version), "fixture", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_parent_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'device.draftDeleted' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<DeviceDraftStore>().Delete(device.Device.Id, created.Version, "fixture", null));
        Assert.Equal(created.Version, store.Read(device.Device.Id).Version);
        Assert.Equal(created.Input.Id, Assert.Single(store.Read(device.Device.Id).Inputs).Id);
    }

    private static string Path(long deviceId) => $"/api/edge-admin/v1/devices/{deviceId}/inputs/";
    private static ModbusPointSettings Point() => new() { Key = "temperature", FunctionCode = 3, Address = 0, Type = "uint16" };
    private static InputDraftRequest Request(string version) => new(version, "fast", "Fast", Modbus: new(5000, [Point()]));
    private static DeviceDraftResult Device(TenonEdgeApplicationFactory factory)
    {
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "plc", "PLC", ConnectionKind.Modbus, Modbus: new("10.0.0.1")), "fixture", null);
        return factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1), "fixture", null);
    }
}
