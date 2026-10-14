using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryCleanupApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Endpoint = "/api/edge-admin/v1/deliveries/cleanup";

    [Fact]
    public async Task CleanupRequiresAdministratorCsrfExplicitConfirmationAndExactAreaAndWorksWithoutAnActiveTarget()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        foreach (var role in new[] { UserRole.Administrator, UserRole.Configurator, UserRole.Viewer })
        { TestAccounts.Create(factory.Services, role.ToString(), ApiRequests.StrongPassword, role); }
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.Equal(RuntimeCoordinatorState.Idle, coordinator.Status.State);
        var failed = await SeedAsync(cache, DeliveryState.Failed);
        var quarantined = await SeedAsync(cache, DeliveryState.Quarantined);
        var pending = await SeedAsync(cache, DeliveryState.Pending);
        var request = new DeliveryCleanupRequest(DeliveryState.Failed, true, [failed]);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        foreach (var name in new[] { "Configurator", "Viewer" })
        {
            using var client = await factory.CreateLoggedInClientAsync(name, Token);
            using var response = await client.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var admin = await factory.CreateLoggedInClientAsync("Administrator", Token);
        var csrf = admin.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await admin.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        admin.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        foreach (var invalid in new[] { request with { Confirmed = false }, request with { State = DeliveryState.Pending },
            request with { Items = null }, request with { Items = [] }, request with { Items = [failed, failed] } })
        {
            using var response = await admin.PostJsonAsync(Endpoint, invalid, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var wrongArea = await admin.PostJsonAsync(Endpoint, request with { Items = [failed, quarantined] }, Token);
        Assert.Equal(HttpStatusCode.Conflict, wrongArea.StatusCode);
        Assert.Single(cache.ReadFailed(11, 0, 100));
        using var cleared = await admin.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.True(cleared.Headers.CacheControl!.NoStore);
        using var result = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, result.RootElement.GetProperty("clearedRecords").GetInt32());
        Assert.Empty(cache.ReadFailed(11, 0, 100));
        Assert.Equal(pending.DeliveryId, Assert.Single(cache.ReadPending(11, 0, 100)).DeliveryId);
        Assert.Equal(quarantined.DeliveryId, Assert.Single(cache.ReadQuarantined(11, 0, 100)).DeliveryId);
        using var replay = await admin.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var isolated = await admin.PostJsonAsync(Endpoint, new DeliveryCleanupRequest(DeliveryState.Quarantined, true, [quarantined]), Token);
        Assert.Equal(HttpStatusCode.OK, isolated.StatusCode);
        Assert.Empty(cache.ReadQuarantined(11, 0, 100));
        Assert.Equal(1, cache.GetPersistentCounters()["manuallyClearedFailedRecords"]);
        Assert.Equal(1, cache.GetPersistentCounters()["manuallyClearedQuarantinedRecords"]);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("acknowledgedRecords"));
        var audit = TestAccounts.Audit(factory.Services, "delivery.cleanup");
        Assert.Equal(2, audit.Count(item => item.Result == "success"));
        Assert.All(audit, item => Assert.DoesNotContain("private-cleanup-payload", item.Detail ?? "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("MES recovery")]
    public async Task CancelledWaitCannotWithdrawCleanupAndStopWaitsForTheOwnedWriteAndAudit(string? tokenName)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var item = await SeedAsync(cache, DeliveryState.Quarantined);
        var selection = new DeliveryCleanupSelection(DeliveryState.Quarantined, [item]);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var write = coordinator.CleanupDeliveriesAsync(selection, new AuditAccount("admin", tokenName), null, caller.Token);
        Assert.True(coordinator.Status.QueueOperationInProgress);
        await Assert.ThrowsAsync<DeliveryCleanupUnavailableException>(() => coordinator.CleanupDeliveriesAsync(selection, "admin", null, Token));
        var version = factory.Services.GetRequiredService<RouteDraftStore>().Read().Version;
        await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync(version, "admin", null, Token));
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        var stop = coordinator.StopAsync(Token);
        Assert.False(stop.IsCompleted);
        transaction.Commit();
        await stop.WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Empty(cache.ReadQuarantined(11, 0, 100));
        Assert.False(coordinator.Status.QueueOperationInProgress);
        Assert.Equal(RuntimeCoordinatorState.Stopped, coordinator.Status.State);
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.cleanup")).Result);
        Assert.Equal(tokenName, Assert.Single(TestAccounts.Audit(factory.Services, "delivery.cleanup")).TokenName);
        await Assert.ThrowsAsync<DeliveryCleanupUnavailableException>(() => coordinator.CleanupDeliveriesAsync(selection, "admin", null, Token));
    }

    [Fact]
    public async Task StorageFailureDoesNotReportDeletedOrLeakStorageExceptionAndCanBeExplicitlyRetried()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var selected = await SeedAsync(cache, DeliveryState.Failed);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER forbid_cleanup BEFORE DELETE ON delivery BEGIN SELECT RAISE(ABORT, 'private-storage-marker'); END;";
        command.ExecuteNonQuery();
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var request = new DeliveryCleanupRequest(DeliveryState.Failed, true, [selected]);
        using var failed = await admin.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("private-storage-marker", await failed.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Single(cache.ReadFailed(11, 0, 100));
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyClearedFailedRecords"));
        var audit = Assert.Single(TestAccounts.Audit(factory.Services, "delivery.cleanup"));
        Assert.Equal("failure", audit.Result);
        Assert.DoesNotContain("private-storage-marker", audit.Detail!, StringComparison.Ordinal);
        command.CommandText = "DROP TRIGGER forbid_cleanup;";
        command.ExecuteNonQuery();
        using var cleared = await admin.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty(cache.ReadFailed(11, 0, 100));
    }

    private static async Task<DeliveryCleanupItem> SeedAsync(DeliveryCache cache, DeliveryState state)
    {
        var draft = new DeliveryDraft(Guid.NewGuid(), 11, Guid.NewGuid(), "meter", 1, "private-cleanup-payload"u8.ToArray(), "fp", "test-owner", "send");
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        if (state == DeliveryState.Failed)
        { await cache.RecordFailureAsync(new(seq, 1, "rejected", true, false), Token); }
        if (state == DeliveryState.Quarantined)
        { await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetMissing, Token); }
        return new(seq, draft.DeliveryId, 11, null);
    }
}
