using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["meter/sample"];

    [Theory]
    [InlineData("normal")]
    [InlineData("cancel")]
    [InlineData("auditFailure")]
    public async Task TwoRealHttpSendsPrecedeOwnedMigrationAndDurableResultAudit(string scenario)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(string Body, string Key)> oldRequests = new(), newRequests = new();
        peer.MapPost("/old/send", context => Receive(context, oldRequests, releaseOld));
        peer.MapPost("/new/send", context => Receive(context, newRequests, releaseNew));
        await peer.StartAsync(Token);
        try
        {
            using var data = TemporaryDirectory.Create();
            using var seeds = TemporaryDirectory.Create();
            var address = new Uri(peer.Urls.Single());
            var seed = Path.Combine(seeds.Path, "seed.json");
            await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                units = new object[] {
                    new { kind = "input.simulator", key = "meter/sample", configuration = new { periodMs = 1000, sample = new { value = 42 } } },
                    new { kind = "output.http", key = "old", configuration = new { connectionId = 11, baseAddress = new Uri(address,"/old/").AbsoluteUri, path = "send", timeoutMs = 60000, inputs = Inputs } },
                    new { kind = "output.http", key = "next", configuration = new { connectionId = 12, baseAddress = new Uri(address,"/new/").AbsoluteUri, path = "send", timeoutMs = 60000, inputs = Inputs } },
                },
            }), Token);
            var settings = new Dictionary<string, string>
            {
                [RuntimeSettings.SeedSnapshotKey] = seed,
                ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture),
            };
            Guid operation;
            int actual;
            await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path, settings))
            {
                using var client = factory.CreateClient();
                var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
                var cache = coordinator.Runtime.Deliveries;
                var controls = factory.Services.GetRequiredService<OutputControlStore>();
                var migrations = factory.Services.GetRequiredService<DeliveryMigrationStore>();
                var database = factory.Services.GetRequiredService<ConfigDatabase>();
                try
                {
                    await OutputPipelineTests.UntilAsync(() => oldRequests.Count == 1 && newRequests.Count == 1 && cache.GetQueueTotals(11).PendingCount >= 2);
                    controls.Write(11, new(true, true), "admin", null);
                    var targetPause = controls.Write(12, new(true, true), "admin", null);
                    var records = cache.ReadPending(11, 0, 1000);
                    var target = cache.ReadPending(12, 0, 1000)[0];
                    var command = DeliveryMigrationCommand.Selected(cache.Options.ConfigStoreId, Guid.NewGuid(), 11, 12,
                        target.Fingerprint, target.FingerprintSummary, records.Select(record => new DeliveryMigrationItem(record.Seq, record.DeliveryId, record.Fingerprint, record.MigrationId)).ToArray());
                    operation = command.OperationId;
                    actual = records.Count - 1; // First original source request will ACK; it must not be migrated.
                    if (scenario == "auditFailure")
                    { database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_migration_finish BEFORE INSERT ON audit_log WHEN NEW.action='delivery.migrationFinished' BEGIN SELECT abs(-9223372036854775808); END;"); }
                    using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    var moving = coordinator.MigrateSelectedDeliveriesAsync(command, 1, records[0].FingerprintSummary, true, "admin", "192.0.2.10", caller.Token);
                    await OutputPipelineTests.UntilAsync(() => migrations.Read(operation)?.Status == "started");
                    Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
                    Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationFinished"));
                    Assert.False(moving.IsCompleted);
                    await Assert.ThrowsAsync<DeliveryMigrationUnavailableException>(() => coordinator.MigrateSelectedDeliveriesAsync(command, 1, "old", true, "admin", null, Token));
                    var drafts = factory.Services.GetRequiredService<RouteDraftStore>().Read();
                    await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync(drafts.Version, "admin", null, Token));
                    releaseOld.TrySetResult();
                    await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == 1);
                    Assert.Null(cache.ReadMigrationReceipt(operation));
                    Assert.False(moving.IsCompleted); // Target's real HTTP request still has no response.
                    Task? stop = null;
                    if (scenario == "cancel")
                    {
                        caller.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
                        stop = coordinator.StopAsync(Token);
                        Assert.False(stop.IsCompleted);
                    }
                    releaseNew.TrySetResult();
                    if (stop is not null)
                    { await stop.WaitAsync(TimeSpan.FromSeconds(15), Token); }
                    else
                    {
                        var result = await moving.WaitAsync(TimeSpan.FromSeconds(15), Token);
                        Assert.Equal(scenario == "auditFailure" ? DeliveryWriteStatus.StorageFailed : DeliveryWriteStatus.Committed, result.Status);
                        if (scenario == "normal")
                        { Assert.Equal(actual, result.AffectedRecords); }
                    }
                    Assert.Equal(actual, cache.ReadMigrationReceipt(operation)!.AffectedRecords);
                    Assert.Single(oldRequests);
                    Assert.Single(newRequests);
                    Assert.True(controls.Read(11).ManuallyPaused);
                    Assert.True(controls.Read(12).ManuallyPaused);
                    Assert.False(coordinator.Status.QueueOperationInProgress);
                    if (scenario == "auditFailure")
                    {
                        Assert.Equal("started", migrations.Read(operation)!.Status);
                        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationFinished"));
                        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationSyncFailed"));
                        database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_migration_finish");
                    }
                    else
                    {
                        var row = migrations.Read(operation)!;
                        Assert.Equal("completed", row.Status);
                        Assert.Equal(actual, row.AffectedRecords);
                        Assert.Equal("admin", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationFinished")).AccountName);
                    }
                    if (scenario == "normal")
                    {
                        controls.Write(12, new(false, true, targetPause.ResumeVersion), "admin", null);
                        var migratedKeys = records.Skip(1).Select(record => record.DeliveryId.ToString("D")).ToArray();
                        await OutputPipelineTests.UntilAsync(() => migratedKeys.All(key => newRequests.Any(request => request.Key == key)));
                        foreach (var record in records.Skip(1))
                        {
                            var received = Assert.Single(newRequests, request => request.Key == record.DeliveryId.ToString("D"));
                            Assert.Equal(System.Text.Encoding.UTF8.GetString(record.Payload.Span), received.Body);
                        }
                        Assert.DoesNotContain(newRequests, request => request.Key == records[0].DeliveryId.ToString("D"));
                    }
                    await coordinator.StopAsync(Token);
                }
                finally { releaseOld.TrySetResult(); releaseNew.TrySetResult(); }
            }
            await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path, settings);
            using var restartedClient = restarted.CreateClient();
            var final = restarted.Services.GetRequiredService<DeliveryMigrationStore>().Read(operation)!;
            Assert.Equal("completed", final.Status);
            Assert.Equal(actual, final.AffectedRecords);
            Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationStarted"));
            Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationFinished"));
            Assert.Equal(actual, restarted.Services.GetRequiredService<DeliveryCache>().GetPersistentCounters()["manuallyMigratedRecords"]);
        }
        finally { releaseOld.TrySetResult(); releaseNew.TrySetResult(); await peer.StopAsync(CancellationToken.None); }
    }

    private static async Task Receive(HttpContext context, ConcurrentQueue<(string Body, string Key)> requests, TaskCompletionSource release)
    {
        using var reader = new StreamReader(context.Request.Body);
        requests.Enqueue((await reader.ReadToEndAsync(Token), context.Request.Headers["Idempotency-Key"].ToString()));
        await release.Task.WaitAsync(Token);
        context.Response.StatusCode = 204;
    }
}
