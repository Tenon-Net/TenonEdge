using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationDraftEndpointTests
{
    private const string Root = "/api/edge-admin/v1/configuration/draft";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimePlanDocument Plan() => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task EveryRoleCanReadSummaryButOnlyEditorsCanConfirmDiscard(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "unused", "Unused", DeviceKind.Simulator), "author", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.ReadPointers();
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var summaryResponse = await client.GetAsync(Root, Token);
        Assert.True(summaryResponse.Headers.CacheControl!.NoStore);
        var summary = await summaryResponse.ReadJsonAsync(Token);
        Assert.True(summary.GetProperty("comparisonAvailable").GetBoolean());
        Assert.Equal(1, summary.GetProperty("changeCount").GetInt32());
        Assert.Equal("device", summary.GetProperty("objects")[0].GetProperty("kind").GetString());
        Assert.Equal(1, summary.GetProperty("objects")[0].GetProperty("added").GetInt32());
        Assert.DoesNotContain("Unused", summary.GetRawText(), StringComparison.Ordinal);
        using var response = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(saved.Version, null, true), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True(response.Headers.CacheControl!.NoStore);
            var result = await response.ReadJsonAsync(Token);
            Assert.NotEqual(saved.Version, result.GetProperty("version").GetString());
            Assert.Equal("configuration.discardDoesNotRestoreCredentials", result.GetProperty("warnings")[0].GetString());
            Assert.Empty(devices.Read().Devices);
            Assert.Equal(0, (await client.GetJsonAsync(Root, Token)).GetProperty("changeCount").GetInt32());
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
            Assert.Equal("operator", audit.AccountName);
            Assert.Equal("configuration.draft", audit.Target);
        }
        else
        { Assert.Single(devices.Read().Devices); Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded)); }
        Assert.Equal(before, versions.ReadPointers());
        Assert.Empty(versions.ReadHistory());
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task DiscardRestoresAllFrozenObjectsReferencesAndMetadataWithoutRestoringCredentials()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var inputs = factory.Services.GetRequiredService<InputDraftStore>();
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var connection = connections.Write(null, new("0", "http", "Frozen HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "author", null);
        var device = devices.Write(null, new(connection.Version, "meter", "Frozen meter", DeviceKind.Simulator), "author", null);
        var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Frozen input", Simulator: new(SampleJson: "{\"value\":42}")), "author", null);
        var graph = new RouteGraph(1, [new("source", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } }))], [], new([]));
        var route = routes.Write(null, new(input.Version, "route", "Frozen route", false, graph), "author", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Plan(), route.Version, "author"), null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var frozenMetadata = database.InTransaction(ConfigurationEditJournal.Read);
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        await credentials.WriteAsync(connection.Connection.Id, new("http://localhost/", HttpCredentialKind.Bearer,
            Secret: "new-immediate-secret", ExpectedDraftVersion: route.Version), "operator", null, true, token: TestContext.Current.CancellationToken);
        var credentialRow = database.Client.Queryable<HttpCredentialRecord>().Single();
        var deletedRoute = routes.Delete(route.Route.Id, route.Version, "editor", null);
        var deletedInput = inputs.Delete(device.Device.Id, input.Input.Id, deletedRoute.Version, "editor", null);
        var modifiedDevice = devices.Write(device.Device.Id, new(deletedInput.Version, "meter", "Edited meter", DeviceKind.Simulator), "editor", null);
        var modifiedConnection = connections.Write(connection.Connection.Id,
            new(modifiedDevice.Version, "http", "Edited HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "editor", null);
        var added = devices.Write(null, new(modifiedConnection.Version, "extra", "Extra", DeviceKind.Simulator), "editor", null);
        var before = versions.ReadPointers();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var delivery = new DeliveryDraft(Guid.NewGuid(), connection.Connection.Id, Guid.NewGuid(), "meter", 1,
            "{\"frozen\":42}"u8.ToArray(), "fixture-fingerprint", "fixture-recipient", "/old-path");
        Assert.True((await cache.EnqueueAsync([delivery], Token)).Committed);
        var queueBefore = cache.GetStatus().CommittedRecords;
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var summary = await client.GetJsonAsync(Root, Token);
        Assert.Equal(5, summary.GetProperty("changeCount").GetInt32());
        var deviceCounts = Assert.Single(summary.GetProperty("objects").EnumerateArray(), item => item.GetProperty("kind").GetString() == "device");
        Assert.Equal(1, deviceCounts.GetProperty("added").GetInt32());
        Assert.Equal(1, deviceCounts.GetProperty("modified").GetInt32());
        using var response = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(added.Version, 1, true), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var version = (await response.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
        Assert.Empty(versions.CompareDraft(1, version));
        Assert.Equal("Frozen HTTP", Assert.Single(connections.Read().Connections).Name);
        Assert.Equal("Frozen meter", Assert.Single(devices.Read().Devices).Name);
        Assert.Equal("Frozen input", Assert.Single(inputs.Read(device.Device.Id).Inputs).Name);
        Assert.Equal("Frozen route", Assert.Single(routes.Read().Routes).Name);
        Assert.NotEmpty(database.Client.Queryable<RouteDraftReferenceRecord>().ToList());
        Assert.Equal(frozenMetadata, database.InTransaction(ConfigurationEditJournal.Read));
        Assert.Equal(credentialRow.ProtectedSecret, database.Client.Queryable<HttpCredentialRecord>().Single().ProtectedSecret);
        Assert.Equal(before, versions.ReadPointers());
        Assert.Equal(queueBefore, cache.GetStatus().CommittedRecords);
        var pending = Assert.Single(cache.ReadPending(connection.Connection.Id, 0, 10));
        Assert.Equal(delivery.DeliveryId, pending.DeliveryId);
        Assert.Equal(1, pending.Revision);
        Assert.Equal("/old-path", pending.TargetAddress);
        Assert.Equal(delivery.Payload.ToArray(), pending.Payload.ToArray());
        Assert.Single(versions.ReadHistory());
        using var repeated = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(added.Version, 1, true), Token);
        await ProblemAssert.HasStatusAsync(repeated, HttpStatusCode.Conflict, Token);
        Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
    }

    [Fact]
    public async Task AuditFailureRollsBackReplacementVersionMetadataAndIdFloor()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "author", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var beforeFloor = database.Client.Ado.GetLong("SELECT id_floor FROM system_state WHERE id = 1");
        var beforeEdits = database.InTransaction(ConfigurationEditJournal.Read);
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER reject_discard BEFORE INSERT ON audit_log WHEN NEW.action = 'configuration.discardDraft' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
        try
        {
            Assert.ThrowsAny<Exception>(() => factory.Services.GetRequiredService<RuntimeVersionStore>().DiscardDraft(saved.Version, null, "operator", null, Token));
            Assert.Equal(saved.Version, devices.Read().Version);
            Assert.Single(devices.Read().Devices);
            Assert.Equal(beforeEdits, database.InTransaction(ConfigurationEditJournal.Read));
            Assert.Equal(beforeFloor, database.Client.Ado.GetLong("SELECT id_floor FROM system_state WHERE id = 1"));
            Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_discard"); }
    }

    [Fact]
    public async Task DiscardKeepsRemovedIdsAboveTheFloorEvenWithoutLegacyEditMetadata()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "author", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("DELETE FROM configuration_edit");
        factory.Services.GetRequiredService<RuntimeVersionStore>().DiscardDraft(device.Version, null, "operator", null, Token);
        Assert.True(database.Client.Ado.GetLong("SELECT id_floor FROM system_state WHERE id = 1") >= device.Device.Id);
    }

    [Fact]
    public async Task ConcurrentConfirmationsCanDiscardOnlyOnce()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "author", null);
        using var first = await factory.CreateLoggedInClientAsync("operator", Token);
        using var second = await factory.CreateLoggedInClientAsync("operator", Token);
        var request = new ConfigurationDiscardRequest(device.Version, null, true);
        var responses = await Task.WhenAll(first.PostJsonAsync(Root + "/discard", request, Token), second.PostJsonAsync(Root + "/discard", request, Token));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
            Assert.Empty(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
        }
        finally { foreach (var response in responses) { response.Dispose(); } }
    }
    [Fact]
    public async Task AnonymousCsrfAndUnconfirmedOrForgedRequestsCannotDiscard()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var summary = await anonymous.GetAsync(Root, Token);
        await ProblemAssert.HasStatusAsync(summary, HttpStatusCode.Unauthorized, Token);
        using var denied = await anonymous.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest("0", null, true), Token);
        await ProblemAssert.HasStatusAsync(denied, HttpStatusCode.Unauthorized, Token);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        foreach (var request in new[] { new ConfigurationDiscardRequest("0", null, false), new(" ", null, true), new("0", 0, true) })
        {
            using var invalid = await client.PostJsonAsync(Root + "/discard", request, Token);
            await ProblemAssert.HasStatusAsync(invalid, HttpStatusCode.BadRequest, Token);
        }
        using var spoofed = await client.PostJsonAsync(Root + "/discard", new { expectedVersion = "0", expectedEffectiveRevision = (long?)null, confirmed = true, account = "forged" }, Token);
        await ProblemAssert.HasStatusAsync(spoofed, HttpStatusCode.BadRequest, Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest("0", null, true), Token);
        await ProblemAssert.HasStatusAsync(csrf, HttpStatusCode.Forbidden, Token);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
    }

    [Fact]
    public async Task ChangedDraftOrEffectiveVersionAndPendingApplicationRejectDiscard()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "author", null);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var staleDraft = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest("0", null, true), Token);
        Assert.Contains("configuration.draftConflict", await ProblemAssert.HasStatusAsync(staleDraft, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var pending = versions.FreezeDraft(Plan(), device.Version, "author");
        Assert.Equal(pending, (await client.GetJsonAsync(Root, Token)).GetProperty("pendingRevision").GetInt64());
        using var busy = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(device.Version, null, true), Token);
        Assert.Contains("runtime.applicationInProgress", await ProblemAssert.HasStatusAsync(busy, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        versions.Commit(pending, null);
        using var changedEffective = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(device.Version, null, true), Token);
        Assert.Contains("configuration.reviewChanged", await ProblemAssert.HasStatusAsync(changedEffective, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        Assert.Equal(device.Version, factory.Services.GetRequiredService<DeviceDraftStore>().Read().Version);
        Assert.Single(versions.ReadHistory());
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrCorruptEffectiveSnapshotCannotSilentlyClearTheDraft(bool corrupt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "author", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        if (corrupt)
        { factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET content_hash = 'private-corrupt-detail' WHERE revision = 1"); }
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var summary = await client.GetAsync(Root, Token);
        if (corrupt)
        { Assert.Contains("configuration.invalidSnapshot", await ProblemAssert.HasStatusAsync(summary, HttpStatusCode.Conflict, Token), StringComparison.Ordinal); }
        else
        {
            var body = await summary.ReadJsonAsync(Token);
            Assert.False(body.GetProperty("comparisonAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("changeCount").ValueKind);
        }
        using var discard = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest(device.Version, 1, true), Token);
        var problem = await ProblemAssert.HasStatusAsync(discard, HttpStatusCode.Conflict, Token);
        Assert.Contains(corrupt ? "configuration.invalidSnapshot" : "configuration.unsupportedSnapshot", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-corrupt-detail", problem, StringComparison.Ordinal);
        Assert.Single(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.DraftDiscarded));
    }

    [Fact]
    public async Task DraftSummaryAndDiscardShareTheGlobalBudget()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var budget = factory.Services.GetRequiredService<TenonEdge.Runtime.Diagnostics.PreviewOperationBudget>();
        var leases = Enumerable.Range(0, 4).Select(i => budget.TryAcquire("other" + i)!).ToArray();
        try
        {
            using var summary = await client.GetAsync(Root, Token);
            await ProblemAssert.HasStatusAsync(summary, HttpStatusCode.TooManyRequests, Token);
            using var discard = await client.PostJsonAsync(Root + "/discard", new ConfigurationDiscardRequest("0", null, true), Token);
            await ProblemAssert.HasStatusAsync(discard, HttpStatusCode.TooManyRequests, Token);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        using var success = await client.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task RealKestrelBoundsDiscardBodyBeforeBinding()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程验证依赖 Linux 的 SIGTERM 与 flock。");
        using var working = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        await using var host = HostProcess.Start(working.Path, "Production", data.Path);
        var timeout = TimeSpan.FromSeconds(30);
        using var client = new HttpClient { BaseAddress = await host.WaitForListeningAddressAsync(timeout, Token), Timeout = timeout };
        await host.WaitUntilReadyAsync(client, timeout, Token);
        using var setup = await client.CompleteSetupAsync(Token);
        await client.UseSessionAsync(setup, Token);
        using var body = new StringContent(new string(' ', 4096) + "{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(Root + "/discard", UriKind.Relative), body, Token);
        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.RequestEntityTooLarge, Token);
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

}
