using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Devices;

public sealed class DeviceDraftCopyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Endpoint(long id) => $"/api/edge-admin/v1/devices/{id}/copy";

    [Theory]
    [InlineData("Modbus")]
    [InlineData("Mqtt")]
    [InlineData("Http")]
    [InlineData("Simulator")]
    public async Task CopiesPersistIndependentSourcesAndDisabledDevicesWithoutChangingRuntime(string kind)
    {
        using var directory = TemporaryDirectory.Create();
        long sourceId;
        long[] copyIds;
        string finalVersion;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            var (device, input) = Seed(factory, Enum.Parse<DeviceKind>(kind));
            sourceId = device.Id;
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            using var response = await admin.PostJsonAsync(Endpoint(sourceId), new DeviceDraftCopyRequest(input.Version, 2), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.ReadJsonAsync(Token);
            finalVersion = result.GetProperty("version").GetString()!;
            copyIds = result.GetProperty("devices").EnumerateArray().Select(copy => copy.GetProperty("id").GetInt64()).ToArray();
            Assert.Equal(2, copyIds.Length);
            var devices = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
            Assert.Equal(3, devices.Devices.Select(copy => copy.Key).Distinct().Count());
            foreach (var copy in devices.Devices.Where(copy => copy.Id != sourceId))
            {
                Assert.False(copy.Enabled);
                Assert.True(BusinessKeys.IsValid(copy.Key));
                Assert.Equal(device with { Id = copy.Id, Key = copy.Key, Enabled = false }, copy);
                var stored = Assert.Single(factory.Services.GetRequiredService<InputDraftStore>().Read(copy.Id).Inputs);
                Assert.NotEqual(input.Input.Id, stored.Id);
                Assert.Equal(JsonSerializer.Serialize(input.Input with { Id = stored.Id, DeviceId = copy.Id }), JsonSerializer.Serialize(stored));
            }
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, "device.draftCopied"));
            Assert.DoesNotContain("9007199254740993", JsonSerializer.Serialize(audit));
            var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(state.EffectiveRevision);
            Assert.Null(state.PendingRevision);
            Assert.Null(state.PreviousRevision);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        var store = restarted.Services.GetRequiredService<DeviceDraftStore>();
        Assert.Equal(finalVersion, store.Read().Version);
        Assert.Equal(3, store.Read().Devices.Length);
        store.Delete(copyIds[0], finalVersion, "fixture", null);
        Assert.Single(restarted.Services.GetRequiredService<InputDraftStore>().Read(sourceId).Inputs);
        Assert.Single(restarted.Services.GetRequiredService<InputDraftStore>().Read(copyIds[1]).Inputs);
    }

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task CopyUsesConfiguratorPolicy(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input) = Seed(factory, DeviceKind.Simulator);
        TestAccounts.Create(factory.Services, "user", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("user", Token);
        using var response = await client.PostJsonAsync(Endpoint(device.Id), new DeviceDraftCopyRequest(input.Version, 1), Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 2 : 1, factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices.Length);
    }

    [Fact]
    public async Task AnonymousCsrfMissingDeviceAndBadCountsHaveNoWrites()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input) = Seed(factory, DeviceKind.Simulator);
        var request = new DeviceDraftCopyRequest(input.Version, 1);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint(device.Id), request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var count in new[] { -1, 0, 21, int.MaxValue })
        {
            using var bad = await admin.PostJsonAsync(Endpoint(device.Id), request with { Count = count }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using var missing = await admin.PostJsonAsync(Endpoint(1), request, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Endpoint(device.Id), request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Equal(input.Version, factory.Services.GetRequiredService<DeviceDraftStore>().Read().Version);
        Assert.Single(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
        Assert.Empty(TestAccounts.Audit(factory.Services, "device.draftCopied"));
    }

    [Fact]
    public async Task TwentyCopiesUseOneVersionAndConcurrentRetriesCannotDuplicateTheBatch()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input) = Seed(factory, DeviceKind.Simulator);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var request = new DeviceDraftCopyRequest(input.Version, 20);
        var responses = await Task.WhenAll(admin.PostJsonAsync(Endpoint(device.Id), request, Token), admin.PostJsonAsync(Endpoint(device.Id), request, Token));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var snapshot = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
        Assert.Equal(21, snapshot.Devices.Length);
        Assert.Equal(21, snapshot.Devices.Select(copy => copy.Key).Distinct().Count());
        Assert.Single(TestAccounts.Audit(factory.Services, "device.draftCopied"));
        Assert.Equal(21, factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<InputDraftRecord>().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureInSecondCopyOrAuditRollsBackWholeBatch(bool auditFailure)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input) = Seed(factory, DeviceKind.Simulator);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand(auditFailure
            ? "CREATE TRIGGER fail_copy BEFORE INSERT ON audit_log WHEN NEW.action = 'device.draftCopied' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;"
            : "CREATE TRIGGER fail_copy BEFORE INSERT ON input_draft WHEN (SELECT COUNT(*) FROM device_draft) > 2 BEGIN SELECT RAISE(ABORT, 'second copy failure'); END;");
        using var response = await admin.PostJsonAsync(Endpoint(device.Id), new DeviceDraftCopyRequest(input.Version, 2), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var snapshot = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
        Assert.Equal(input.Version, snapshot.Version);
        Assert.Single(snapshot.Devices);
        Assert.Equal(1, db.Client.Queryable<InputDraftRecord>().Count());
        Assert.Empty(TestAccounts.Audit(factory.Services, "device.draftCopied"));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("effective")]
    [InlineData("previous")]
    [InlineData("pending")]
    public async Task GeneratedKeysSkipDraftAndRetainedSnapshotCollisions(string reference)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input) = Seed(factory, DeviceKind.Simulator);
        const long floor = 8000000000000000; // Test-only future logical timestamp makes the next ID deterministic.
        var key = DeviceDraftStore.CopyKey(device.Key, floor + 1);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        var version = input.Version;
        if (reference == "draft")
        {
            version = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
                new(version, key, "Collision", DeviceKind.Simulator), "fixture", null).Version;
        }
        else
        {
            var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"units\":[{\"kind\":\"input.simulator\",\"key\":\"" + key + "/input\",\"configuration\":{\"periodMs\":5000}}]}"));
            db.InTransaction(client =>
            {
                client.Insertable(new RuntimeSnapshotRecord
                {
                    Revision = 1,
                    SchemaVersion = 1,
                    Content = plan.CanonicalText,
                    ContentHash = plan.ContentHash,
                    Origin = RuntimeSnapshotOrigins.Seed,
                    State = RuntimeSnapshotStates.Effective,
                    CreatedBy = "fixture"
                }).ExecuteCommand();
                var state = client.Queryable<SystemStateRecord>().InSingle(1);
                if (reference == "effective")
                { state.EffectiveRevision = 1; }
                if (reference == "previous")
                { state.PreviousRevision = 1; }
                if (reference == "pending")
                { state.PendingRevision = 1; }
                client.Updateable(state).ExecuteCommand();
                return true;
            });
        }
        // No DI/runtime registration: deterministic IDs only for this isolated store operation.
        var ids = new SnowflakeIdGenerator(floor, DateTimeOffset.UnixEpoch, TimeProvider.System, NullLogger.Instance);
        var store = new DeviceDraftStore(db, ids, factory.Services.GetRequiredService<AuditTrail>(), TimeProvider.System);
        var result = store.Copy(device.Id, new(version, 1), "fixture", null);
        var copy = Assert.Single(result.Devices);
        Assert.Equal(floor + 2, copy.Id);
        Assert.NotEqual(key, copy.Key);
    }

    [Fact]
    public async Task EmptyDeviceCanBeCopiedWithoutInventingSources()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<DeviceDraftStore>();
        var source = store.Write(null, new("0", "empty", "Empty", DeviceKind.Simulator), "fixture", null);
        var result = store.Copy(source.Device.Id, new(source.Version, 1), "fixture", null);
        Assert.Empty(factory.Services.GetRequiredService<InputDraftStore>().Read(Assert.Single(result.Devices).Id).Inputs);
    }

    private static (DeviceDraft Device, InputDraftResult Input) Seed(TenonEdgeApplicationFactory factory, DeviceKind kind)
    {
        var version = "0";
        long? connectionId = null;
        if (kind != DeviceKind.Simulator)
        {
            var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new(version, "connection", "Connection", Enum.Parse<ConnectionKind>(kind.ToString()),
                    Modbus: kind == DeviceKind.Modbus ? new("10.0.0.1") : null,
                    Mqtt: kind == DeviceKind.Mqtt ? new("10.0.0.1") : null,
                    Http: kind == DeviceKind.Http ? new("https://example.test/api/") : null), "fixture", null);
            version = connection.Version;
            connectionId = connection.Connection.Id;
        }
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(version, new string('a', 64), "设备", kind, "工厂", connectionId, kind == DeviceKind.Modbus ? 255 : null), "fixture", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "fast", "Fast",
                Modbus: kind == DeviceKind.Modbus ? new(1000, [new() { Key = "value", Address = 0, FunctionCode = 3, Type = "uint16" }]) : null,
                Mqtt: kind == DeviceKind.Mqtt ? new("plant/meter", [new("value", "/value", FieldType.Number)]) : null,
                Http: kind == DeviceKind.Http ? new("values", [new("value", "/value", FieldType.Number)]) : null,
                Simulator: kind == DeviceKind.Simulator ? new(SampleJson: """{"value":9007199254740993}""") : null), "fixture", null);
        return (device.Device, input);
    }
}
