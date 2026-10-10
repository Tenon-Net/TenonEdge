using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeRollbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RollbackCreatesNewVersionRestoresAllDraftTablesAndKeepsCurrentCredential()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory, credential: true);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        var old = credentials.Read(saved.ConnectionId)!;
        credentials.Write(saved.ConnectionId, new("http://localhost/", HttpCredentialKind.Bearer, Secret: "new-current-secret",
            ExpectedVersion: old.Version, ExpectedDraftVersion: saved.Version), "operator", null, true);
        var result = await coordinator.RollbackAsync(1, saved.Version, "operator", "127.0.0.1", Token);
        Assert.True(result.Committed && result.Activated);
        Assert.True(result.DraftReplaced);
        Assert.Equal(3, result.Revision);
        Assert.Equal("runtime.rolledBack", result.Code);
        Assert.Equal(versions.Load(1)!.Content, versions.Load(3)!.Content);
        Assert.Equal(RuntimeSnapshotOrigins.Rollback, versions.Load(3)!.Origin);
        Assert.Equal(3, versions.ReadPointers().RunningRevision);
        Assert.Equal(3, coordinator.Runtime.ActiveRevision);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
        Assert.Equal("Original", Assert.Single(devices.Devices).Name);
        Assert.NotEqual(saved.Version, devices.Version);
        Assert.Empty(versions.CompareDraft(3, devices.Version));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        Assert.Equal(2, database.Client.Queryable<RouteDraftReferenceRecord>().Count());
        Assert.Single(database.Client.Queryable<InputDraftRecord>().ToList());
        Assert.Equal("new-current-secret", credentials.ResolveHttp(saved.ConnectionId, "http://localhost/")!.Secret);
        Assert.DoesNotContain("new-current-secret", versions.Load(3)!.Content, StringComparison.Ordinal);
        var audit = Assert.Single(database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == AuditActions.SnapshotRolledBack).ToList());
        Assert.Equal("operator", audit.AccountName);
        Assert.Equal("r3", audit.Target);
    }

    [Fact]
    public async Task RollbackReviewUsesTargetContentButDoesNotReplaceDraftAllocateVersionOrReadBackSecrets()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory, credential: true);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var pointers = versions.ReadPointers();
        var review = versions.ReviewRollback(1, saved.Version, Token);
        Assert.True(review.PreflightPassed);
        Assert.Equal(1, review.TargetRevision);
        Assert.Equal(2, review.EffectiveRevision);
        Assert.Equal(saved.Version, review.Version);
        Assert.Equal("Updated", Assert.Single(review.Changes).Before!.Value.GetProperty("name").GetString());
        Assert.Equal("Original", Assert.Single(review.Changes).After!.Value.GetProperty("name").GetString());
        Assert.Contains("configuration.rollbackDoesNotRetractSentData", review.Warnings);
        Assert.Contains("configuration.rollbackKeepsConcurrentDraftEdits", review.Warnings);
        Assert.Contains("configuration.rollbackUsesCurrentCredentials", review.Warnings);
        Assert.DoesNotContain("initial-secret", JsonSerializer.Serialize(review), StringComparison.Ordinal);
        Assert.Equal(pointers, versions.ReadPointers());
        Assert.Equal(2, versions.ReadHistory().Length);
        Assert.Equal("Updated", Assert.Single(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices).Name);
    }

    [Fact]
    public async Task DirtyDraftRejectsRollbackWithoutAllocatingOrChangingRuntime()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory);
        var edited = factory.Services.GetRequiredService<DeviceDraftStore>().Write(saved.DeviceId,
            new(saved.Version, "meter", "Unapplied", DeviceKind.Simulator), "editor", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var error = await Assert.ThrowsAsync<DraftWriteException>(() => coordinator.RollbackAsync(1, edited.Version, "operator", null, Token));
        Assert.Equal("configuration.unappliedChanges", error.Code);
        Assert.Equal(error.Code, Assert.Throws<DraftWriteException>(() => factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewRollback(1, edited.Version, Token)).Code);
        Assert.Equal(2, coordinator.Runtime.ActiveRevision);
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Null(store.Load(3));
        Assert.Null(store.ReadPointers().PendingRevision);
    }

    [Fact]
    public async Task DraftEditedAfterFreezeIsPreservedWhenRollbackCommits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory);
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var frozen = store.FreezeManualRollback(1, saved.Version, "operator");
        var edited = factory.Services.GetRequiredService<DeviceDraftStore>().Write(saved.DeviceId,
            new(saved.Version, "meter", "Concurrent edit", DeviceKind.Simulator), "editor", null);
        Assert.False(store.Commit(frozen.Revision, null, markRunning: true, replaceDraftVersion: saved.Version));
        Assert.Equal(3, store.ReadPointers().EffectiveRevision);
        var draft = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
        Assert.Equal(edited.Version, draft.Version);
        Assert.Equal("Concurrent edit", Assert.Single(draft.Devices).Name);
        Assert.Single(store.CompareDraft(3, draft.Version));
    }

    [Fact]
    public async Task ReplacementWriteFailureRollsBackPointersAuditAndAllDraftRowsThenCanRetry()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER deny_replacement BEFORE INSERT ON device_draft BEGIN SELECT RAISE(ABORT, 'fixture-replacement'); END");
        try
        {
            var failed = await coordinator.RollbackAsync(1, saved.Version, "operator", null, Token);
            Assert.False(failed.Committed);
            Assert.Equal("runtime.commitFailed", failed.Code);
            Assert.Equal(2, store.ReadPointers().EffectiveRevision);
            Assert.Equal(2, store.ReadPointers().RunningRevision);
            Assert.Equal(2, coordinator.Runtime.ActiveRevision);
            Assert.Null(store.ReadPointers().PendingRevision);
            var draft = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
            Assert.Equal(saved.Version, draft.Version);
            Assert.Equal("Updated", Assert.Single(draft.Devices).Name);
            Assert.Equal(2, database.Client.Queryable<RouteDraftReferenceRecord>().Count());
            Assert.Empty(database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == AuditActions.SnapshotRolledBack).ToList());
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER deny_replacement"); }
        var retried = await coordinator.RollbackAsync(1, saved.Version, "operator", null, Token);
        Assert.True(retried.Activated && retried.DraftReplaced == true);
        Assert.Equal(4, retried.Revision);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("recipient")]
    public async Task RequiredCredentialsAreCheckedAgainAtCommit(string damage)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory, credential: true);
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var frozen = store.FreezeManualRollback(1, saved.Version, "operator");
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand(damage switch
        {
            "missing" => "DELETE FROM http_credential",
            "corrupt" => "UPDATE http_credential SET protected_secret = 'fixture-invalid-ciphertext'",
            _ => "UPDATE http_credential SET recipient = 'http://different.invalid/'",
        });
        var error = Assert.Throws<DraftWriteException>(() => store.Commit(frozen.Revision, null, markRunning: true, replaceDraftVersion: saved.Version));
        Assert.Equal("configuration.rollbackCredentialMissing", error.Code);
        Assert.Equal(2, store.ReadPointers().EffectiveRevision);
        Assert.Equal(saved.Version, factory.Services.GetRequiredService<DeviceDraftStore>().Read().Version);
        store.Abandon(frozen.Revision, "fixture");
        var initialCheck = Assert.Throws<DraftWriteException>(() => store.FreezeManualRollback(1, saved.Version, "operator"));
        Assert.Equal("configuration.rollbackCredentialMissing", initialCheck.Code);
        Assert.Equal(initialCheck.Code, Assert.Throws<DraftWriteException>(() => store.ReviewRollback(1, saved.Version, Token)).Code);
        Assert.Null(store.Load(4));
    }

    [Fact]
    public async Task OldCheckpointWithoutCredentialReferencesIsNotAssumedAnonymous()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory);
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var target = store.Load(1)!;
        var json = JsonNode.Parse(target.Content)!;
        Assert.True(json["draft"]!.AsObject().Remove("credentialConnections"));
        target.Content = json.ToJsonString();
        target.ContentHash = RuntimePlanDocument.HashOf(target.Content);
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Updateable(target).ExecuteCommand();
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.Services.GetRequiredService<RuntimeCoordinator>()
            .RollbackAsync(1, saved.Version, "operator", null, Token));
        Assert.Equal(2, store.ReadPointers().EffectiveRevision);
        Assert.Null(store.Load(3));
        Assert.Throws<NotSupportedException>(() => store.ReviewRollback(1, saved.Version, Token));
    }

    [Fact]
    public async Task MissingConnectionCannotBeResurrectedByRollback()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var saved = await SeedAsync(factory);
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var deleted = routes.Delete(saved.RouteId, saved.Version, "operator", null);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var version = connections.Delete(saved.ConnectionId, deleted.Version, "operator", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        await coordinator.ApplyDraftAsync(version.Version, "operator", null, Token);
        var error = await Assert.ThrowsAsync<DraftWriteException>(() => coordinator.RollbackAsync(1, version.Version, "operator", null, Token));
        Assert.Equal("configuration.rollbackConnectionMissing", error.Code);
        Assert.Equal(3, coordinator.Runtime.ActiveRevision);
        Assert.Null(factory.Services.GetRequiredService<RuntimeVersionStore>().Load(4));
    }

    [Theory]
    [InlineData("EXAMPLE.invalid")]
    [InlineData("::1")]
    public async Task MqttCredentialReferencesUseTheSameCanonicalRecipientAsTheAdapter(string host)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "mqtt", "MQTT", ConnectionKind.Mqtt, Enabled: false, Mqtt: new(host)), "operator", null);
        var recipient = new MqttConnectionSettings { ConnectionId = connection.Connection.Id, Host = connection.Connection.Mqtt!.Host }.Recipient;
        factory.Services.GetRequiredService<MqttCredentialStore>().Write(connection.Connection.Id,
            new(recipient, Username: "fixture", Password: "fixture-password", ExpectedDraftVersion: connection.Version), "operator", null, true);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(connection.Version, "operator", null, Token)).Activated);
        Assert.True((await coordinator.ApplyDraftAsync(connection.Version, "operator", null, Token)).Activated);
        var rollback = await coordinator.RollbackAsync(1, connection.Version, "operator", null, Token);
        Assert.True(rollback.Activated && rollback.DraftReplaced == true);
        Assert.Equal(3, rollback.Revision);
    }

    private static async Task<Saved> SeedAsync(TenonEdgeApplicationFactory factory, bool credential = false)
    {
        using var client = factory.CreateClient();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Original", DeviceKind.Simulator), "operator", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"value\":42}")), "operator", null);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "operator", null);
        if (credential)
        {
            factory.Services.GetRequiredService<HttpCredentialStore>().Write(connection.Connection.Id,
                new("http://localhost/", HttpCredentialKind.Bearer, Secret: "initial-secret", ExpectedDraftVersion: connection.Version), "operator", null, true);
        }
        var graph = new RouteGraph(1,
            [new("source", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("output", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("source", "output")], new([]));
        var route = factory.Services.GetRequiredService<RouteDraftStore>().Write(null,
            new(connection.Version, "route", "Route", false, graph), "operator", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(route.Version, "operator", null, Token)).Activated);
        var edited = devices.Write(device.Device.Id, new(route.Version, "meter", "Updated", DeviceKind.Simulator), "operator", null);
        Assert.True((await coordinator.ApplyDraftAsync(edited.Version, "operator", null, Token)).Activated);
        return new(edited.Version, device.Device.Id, connection.Connection.Id, route.Route.Id);
    }

    private sealed record Saved(string Version, long DeviceId, long ConnectionId, long RouteId);
}
