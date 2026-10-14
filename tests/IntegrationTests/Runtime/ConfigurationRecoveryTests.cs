using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ConfigurationRecoveryTests
{
    private const string Root = "/api/edge-admin/v1/configuration/versions/1";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimePlanDocument Empty => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task OnlyAdministratorCanRecoverAndDirtyDraftIsNeverReplaced(string roleName)
    {
        var role = Enum.Parse<UserRole>(roleName);
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path, role);
        string draftJson;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = await factory.CreateLoggedInClientAsync("operator", Token);
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
            Assert.Null(coordinator.Runtime.ActiveRevision);
            var draft = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
            draftJson = JsonSerializer.Serialize(draft);
            using var preflight = await client.PostJsonAsync(Root + "/recovery-review", new { expectedVersion = draft.Version }, Token);
            if (role != UserRole.Administrator)
            {
                Assert.Equal(HttpStatusCode.Forbidden, preflight.StatusCode);
                using var forbidden = await client.PostJsonAsync(Root + "/recover", new ConfigurationRecoveryRequest(draft.Version, "sha256:" + new string('0', 64), true, true), Token);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
            Assert.True(preflight.Headers.CacheControl!.NoStore);
            var review = await preflight.ReadJsonAsync(Token);
            Assert.True(review.GetProperty("preservesDraft").GetBoolean());
            Assert.True(review.GetProperty("canConfirm").GetBoolean());
            Assert.Equal(2, versions.ReadHistory().Length);
            Assert.Empty(coordinator.ReadApplications());
            using var recovery = await client.PostJsonAsync(Root + "/recover", Request(review), Token);
            Assert.Equal(HttpStatusCode.Accepted, recovery.StatusCode);
            var status = await Finished(factory, client, recovery);
            Assert.Equal("recovery", status.GetProperty("kind").GetString());
            Assert.True(status.GetProperty("result").GetProperty("committed").GetBoolean());
            Assert.True(status.GetProperty("result").GetProperty("activated").GetBoolean());
            Assert.False(status.GetProperty("result").GetProperty("draftReplaced").GetBoolean());
            Assert.Equal(3, coordinator.Runtime.ActiveRevision);
            Assert.Equal(draftJson, JsonSerializer.Serialize(factory.Services.GetRequiredService<DeviceDraftStore>().Read()));
            Assert.Equal(versions.Load(1)!.Content, versions.Load(3)!.Content);
            Assert.Equal(RuntimeSnapshotOrigins.Recovery, versions.Load(3)!.Origin);
            Assert.Equal("{}", versions.Load(2)!.Content);
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.SnapshotRecovered));
            Assert.Equal("operator", audit.AccountName);
            using var replay = await client.PostJsonAsync(Root + "/recover", Request(review), Token);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Single(coordinator.ReadApplications());
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        Assert.Equal(3, restarted.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Equal(draftJson, JsonSerializer.Serialize(restarted.Services.GetRequiredService<DeviceDraftStore>().Read()));
        Assert.Single(TestAccounts.Audit(restarted.Services, AuditActions.SnapshotRecovered));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("damage")]
    [InlineData("target")]
    [InlineData("repair")]
    [InlineData("pending")]
    public async Task ConfirmationRechecksDraftDamagedBytesAndHistoricalTarget(string change)
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var review = await Review(factory, client);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand(change switch
        {
            "draft" => "UPDATE configuration_draft SET version='changed'",
            "damage" => "UPDATE runtime_snapshot SET content='[]' WHERE revision=2",
            "target" => "UPDATE runtime_snapshot SET content='[]' WHERE revision=1",
            "repair" => "UPDATE runtime_snapshot SET content=(SELECT content FROM runtime_snapshot WHERE revision=1), content_hash=(SELECT content_hash FROM runtime_snapshot WHERE revision=1) WHERE revision=2",
            _ => "UPDATE system_state SET pending_revision=99",
        });
        using var response = await client.PostJsonAsync(Root + "/recover", Request(review), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.SnapshotRecovered));
        Assert.Equal(2, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory().Length);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task BothConfirmationsAreRequired(bool confirmed, bool preserve)
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var request = Request(await Review(factory, client)) with { Confirmed = confirmed, PreserveDraftConfirmed = preserve };
        using var response = await client.PostJsonAsync(Root + "/recover", request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task AuditFailureLeavesCorruptEffectiveAndDirtyDraftUntouched()
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var review = await Review(factory, client);
        var drafts = factory.Services.GetRequiredService<DeviceDraftStore>();
        var before = JsonSerializer.Serialize(drafts.Read());
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "CREATE TRIGGER fail_recovery BEFORE INSERT ON audit_log WHEN NEW.action='runtime.snapshotRecovered' BEGIN SELECT RAISE(ABORT,'private-fixture'); END;");
        using var response = await client.PostJsonAsync(Root + "/recover", Request(review), Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await Finished(factory, client, response);
        Assert.False(result.GetProperty("result").GetProperty("committed").GetBoolean());
        Assert.DoesNotContain("private-fixture", result.GetRawText(), StringComparison.Ordinal);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.Equal(RuntimeSnapshotStates.NotEffective, versions.Load(3)!.State);
        Assert.Equal(before, JsonSerializer.Serialize(drafts.Read()));
        Assert.Null(factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.SnapshotRecovered));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    public async Task UnknownQueuedRecipientCannotBeBypassedByRecovery(string state)
    {
        using var data = TemporaryDirectory.Create();
        await Initialize(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var oldReview = await Review(factory, client);
        var queue = factory.Services.GetRequiredService<DeliveryCache>();
        var id = Guid.NewGuid();
        var sequence = Assert.Single((await queue.EnqueueAsync([new(id, 99, Guid.NewGuid(), "meter", 2, "{}"u8.ToArray(), "old-fp", "old-summary", "events")], Token)).Seqs);
        if (state == "failed")
        { await queue.RecordFailureAsync(new(sequence, 0, "rejected", true, false), Token); }
        var review = await Review(factory, client);
        Assert.False(review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(1, review.GetProperty("queue").GetProperty("blockedCount").GetInt64());
        using var response = await client.PostJsonAsync(Root + "/recover", Request(oldReview), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, queue.GetQueueReport([]).Usage.PendingCount + queue.GetQueueReport([]).Usage.FailedCount);
        Assert.Equal(id, (state == "pending" ? queue.ReadPending(99, 0, 1) : queue.ReadFailed(99, 0, 1)).Single().DeliveryId);
    }

    private static ConfigurationRecoveryRequest Request(JsonElement review) => new(review.GetProperty("draftVersion").GetString()!,
        review.GetProperty("confirmationHash").GetString()!, true, true);
    private static async Task<JsonElement> Review(FixedDataDirectoryApplicationFactory factory, HttpClient client)
    {
        var version = factory.Services.GetRequiredService<DeviceDraftStore>().Read().Version;
        using var response = await client.PostJsonAsync(Root + "/recovery-review", new { expectedVersion = version }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(Token);
    }
    private static async Task<JsonElement> Finished(FixedDataDirectoryApplicationFactory factory, HttpClient client, HttpResponseMessage response)
    {
        var id = (await response.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        await OutputPipelineTests.UntilAsync(() => factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplication(id)?.FinishedAt is not null);
        return await client.GetJsonAsync(response.Headers.Location!.OriginalString, Token);
    }
    internal static async Task Initialize(string path, UserRole role = UserRole.Administrator)
    {
        await using (var factory = new FixedDataDirectoryApplicationFactory(path))
        {
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, role);
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
            var first = devices.Write(null, new("0", "meter", "Historical", DeviceKind.Simulator, Enabled: false), "operator", null);
            versions.Commit(versions.FreezeDraft(Empty, first.Version, "operator"), null);
            var second = devices.Write(first.Device.Id, new(first.Version, "meter", "Effective", DeviceKind.Simulator, Enabled: false), "operator", null);
            versions.Commit(versions.FreezeDraft(Empty, second.Version, "operator"), null);
            devices.Write(first.Device.Id, new(second.Version, "meter", "Unapplied", DeviceKind.Simulator, Enabled: false), "operator", null);
        }
        new RuntimeStateReader(path).Execute("UPDATE runtime_snapshot SET content='{}' WHERE revision=2");
    }
}
