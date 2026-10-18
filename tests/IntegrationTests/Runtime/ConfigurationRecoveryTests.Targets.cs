using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ConfigurationRecoveryTests
{
    private static readonly string[] RecoveryInputs = ["meter/sample"];
    [Theory]
    [InlineData("http", false, false)]
    [InlineData("http", true, false)]
    [InlineData("mqtt", false, false)]
    [InlineData("mqtt", true, false)]
    [InlineData("http", true, true)]
    [InlineData("mqtt", true, true)]
    public async Task FrozenIdentityAndExpandedAddressAreCheckedWithoutSendingOrChangingQueuedRecords(string protocol, bool formatted, bool outputModule)
    {
        using var data = TemporaryDirectory.Create();
        var id = await InitializeConnection(data.Path, protocol, formatted, outputModule: outputModule);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        { if (outputModule) { services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule(protocol)); } });
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var review = await Review(factory, client);
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        var target = Assert.Single(review.GetProperty("targets").EnumerateArray());
        var fingerprint = target.GetProperty("identity").GetProperty("fingerprint").GetString()!;
        var queue = factory.Services.GetRequiredService<DeliveryCache>();
        var record = new DeliveryDraft(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 2, "{\"frozen\":9223372036854775807}"u8.ToArray(),
            fingerprint, "frozen identity", protocol == "http" ? "events" : "meter/events", protocol == "mqtt" ? 1 : null);
        var sequence = Assert.Single((await queue.EnqueueAsync([record], Token)).Seqs);
        var before = Assert.Single(queue.ReadPending(id, 0, 1));
        var matched = await Review(factory, client);
        Assert.True(matched.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(1, matched.GetProperty("queue").GetProperty("checkedCount").GetInt64());
        // A failed frozen record has exactly the same target restrictions as a pending one.
        await queue.RecordFailureAsync(new(sequence, 0, "rejected", true, false), Token);
        Assert.True((await Review(factory, client)).GetProperty("canConfirm").GetBoolean());
        var wrongPath = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 2,
            "{}"u8.ToArray(), fingerprint, "identity", "other/path")], Token)).Seqs);
        Assert.Equal(!formatted, (await Review(factory, client)).GetProperty("canConfirm").GetBoolean());
        await queue.DeleteAsync([wrongPath], Token);
        var wrongAccount = Assert.Single((await queue.EnqueueAsync([new(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 2,
            "{}"u8.ToArray(), "other-account", "identity", "events")], Token)).Seqs);
        Assert.False((await Review(factory, client)).GetProperty("canConfirm").GetBoolean());
        await queue.QuarantineAsync(wrongAccount, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.True((await Review(factory, client)).GetProperty("canConfirm").GetBoolean());
        Assert.Single(queue.ReadQuarantined(id, 0, 10));
        var after = Assert.Single(queue.ReadFailed(id, 0, 10));
        Assert.Equal(before.DeliveryId, after.DeliveryId);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(before.TargetAddress, after.TargetAddress);
        Assert.Null(factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Equal(2, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministratorIsRecheckedInsideFreezeAndCommitTransactions(bool afterFreeze)
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var captured = await coordinator.ReviewRecoveryAsync(1, versions.ReadRecoveryDraftVersion(), Actor(factory), Token);
        var revision = afterFreeze ? versions.FreezeRecovery(captured.Authorization, "operator", null).Revision : 0;
        TestAccounts.Update(factory.Services, "operator", user => user.IsEnabled = false);
        if (afterFreeze)
        {
            Assert.Throws<ApiTokenException>(() => versions.Commit(revision, new(AuditActions.SnapshotRecovered, AuditResults.Success, "operator", null),
                markRunning: true, recovery: captured.Authorization));
        }
        else
        { Assert.Throws<ApiTokenException>(() => versions.FreezeRecovery(captured.Authorization, "operator", null)); }
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.SnapshotRecovered));
        Assert.Equal(afterFreeze ? 3 : 2, versions.ReadHistory().Length);
    }

    [Fact]
    public async Task NewDraftEditsAfterFreezeRemainCompleteAndCommitCannotOmitRecoveryProof()
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var captured = await coordinator.ReviewRecoveryAsync(1, versions.ReadRecoveryDraftVersion(), Actor(factory), Token);
        var frozen = versions.FreezeRecovery(captured.Authorization, "operator", null);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var draft = devices.Read();
        var device = Assert.Single(draft.Devices);
        devices.Write(device.Id, new(draft.Version, device.Key, "Edited after freeze", device.Kind, Enabled: false), "editor", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var expected = database.InTransaction(client => JsonSerializer.Serialize(ConfigurationCheckpoint.Read(client, ConfigurationDraft.ReadVersion(client))));
        Assert.Throws<InvalidOperationException>(() => versions.Commit(frozen.Revision, null, markRunning: true));
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
        Assert.False(versions.Commit(frozen.Revision, new(AuditActions.SnapshotRecovered, AuditResults.Success, "operator", null),
            markRunning: true, recovery: captured.Authorization));
        Assert.Equal(expected, database.InTransaction(client => JsonSerializer.Serialize(ConfigurationCheckpoint.Read(client, ConfigurationDraft.ReadVersion(client)))));
        Assert.Equal(3, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(versions.Load(1)!.Content, versions.Load(3)!.Content);
    }

    [Fact]
    public async Task CredentialRotationInvalidatesConfirmationAndMissingSecretRefusesPreflight()
    {
        using var data = TemporaryDirectory.Create();
        var id = await InitializeConnection(data.Path, "http", false, credential: true);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var review = await Review(factory, client);
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        var old = credentials.Read(id)!;
        await credentials.WriteAsync(id, new("http://192.0.2.1/", TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer,
            Secret: "rotated-fixture-secret", ExpectedVersion: old.Version, ExpectedDraftVersion: review.GetProperty("draftVersion").GetString()), "operator", null, true, token: TestContext.Current.CancellationToken);
        using var stale = await client.PostJsonAsync(Root + "/recover", Request(review), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var changed = await Review(factory, client);
        Assert.True(changed.GetProperty("canConfirm").GetBoolean());
        Assert.DoesNotContain("rotated-fixture-secret", changed.GetRawText(), StringComparison.Ordinal);
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE http_credential SET protected_secret=NULL");
        using var unavailable = await client.PostJsonAsync(Root + "/recovery-review", new { expectedVersion = changed.GetProperty("draftVersion").GetString() }, Token);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("notEffective")]
    [InlineData("missing")]
    public async Task RecoveryTargetMustBeCompleteIntactAndPreviouslyEffective(string target)
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        if (target == "legacy")
        {
            database.Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET schema_version=1, content=@content, content_hash=@hash WHERE revision=1",
                new { content = Empty.CanonicalText, hash = Empty.ContentHash });
        }
        else
        { database.Client.Ado.ExecuteCommand(target == "missing" ? "DELETE FROM runtime_snapshot WHERE revision=1" : "UPDATE runtime_snapshot SET state='notEffective' WHERE revision=1"); }
        using var response = await client.PostJsonAsync(Root + "/recovery-review", new { expectedVersion = (string?)null }, Token);
        Assert.Equal(target == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    private static ManagementActor Actor(FixedDataDirectoryApplicationFactory factory)
    {
        var user = TestAccounts.Find(factory.Services, "operator")!;
        return new(user.Id, user.SecurityStamp, "127.0.0.1");
    }

    private static async Task<long> InitializeConnection(string path, string protocol, bool formatted, bool credential = false, bool outputModule = false)
    {
        long id;
        await using (var factory = new FixedDataDirectoryApplicationFactory(path, configureServices: services =>
        { if (outputModule) { services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule(protocol)); } }))
        {
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
            var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null, protocol == "http"
                ? new("0", "target", "Target", ConnectionKind.Http, Http: new("http://192.0.2.1/"))
                : new("0", "target", "Target", ConnectionKind.Mqtt, Mqtt: new("192.0.2.1")), "operator", null);
            id = connection.Connection.Id;
            if (credential)
            {
                await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(id, new("http://192.0.2.1/", TenonEdge.Abstractions.Contracts.HttpCredentialKind.Bearer,
                    Secret: "initial-fixture-secret", ExpectedDraftVersion: connection.Version), "operator", null, true, token: TestContext.Current.CancellationToken);
            }
            var plan = Empty;
            if (formatted)
            {
                var output = protocol == "http" ? JsonSerializer.SerializeToElement(new { connectionId = id, baseAddress = "http://192.0.2.1/", path = "events", inputs = RecoveryInputs })
                    : JsonSerializer.SerializeToElement(new { connectionId = id, host = "192.0.2.1", topic = "{deviceKey}/events", inputs = RecoveryInputs });
                plan = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    units = new[] {
                    new { kind = "input.simulator", key = "meter/sample", configuration = JsonSerializer.SerializeToElement(new { periodMs = 1000, seed = 42 }) },
                    new { kind = "output." + protocol, key = "target", configuration = output } }
                }));
            }
            if (outputModule)
            {
                var module = new TestProtocolOutputModule(protocol);
                var config = protocol == "http" ? JsonSerializer.SerializeToElement(new { baseAddress = "http://192.0.2.1/" })
                    : JsonSerializer.SerializeToElement(new { host = "192.0.2.1" });
                var output = module.Unit(config, protocol == "http" ? "events" : "{deviceKey}/events", id, inputs: RecoveryInputs);
                plan = RuntimePlanDocument.Parse(TestProtocolOutputModule.Document([.. plan.Units.Where(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal)), output]),
                    outputs: factory.Services.GetRequiredService<OutputModuleCatalog>());
            }
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(plan, connection.Version, "operator"), null);
            versions.Commit(versions.FreezeDraft(plan, connection.Version, "operator"), null);
        }
        new RuntimeStateReader(path).Execute("UPDATE runtime_snapshot SET content='{}' WHERE revision=2");
        return id;
    }
}
