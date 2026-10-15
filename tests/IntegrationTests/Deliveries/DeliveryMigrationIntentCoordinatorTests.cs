using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationIntentCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OriginalHttpSenderSendsFrozenPendingAfterMigrationButOriginalFailedStaysFailedAndPausePersists()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        ConcurrentQueue<(string Body, string Key)> received = new();
        peer.MapPost("/new/send", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            received.Enqueue((await reader.ReadToEndAsync(Token), context.Request.Headers["Idempotency-Key"].ToString()));
            context.Response.StatusCode = 204;
        });
        await peer.StartAsync(Token);
        using var data = TemporaryDirectory.Create();
        var address = new Uri(peer.Urls.Single());
        DeliveryMigrationIntentDraft intent;
        StoredDelivery pending, failed;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            intent = await MigrationIntentFixture.RegisterAsync(setup.Services, new Uri(address, "/new/").AbsoluteUri);
            var queue = setup.Services.GetRequiredService<DeliveryCache>();
            var records = queue.ReadPending(1, 0, 10);
            await queue.RecordFailureAsync(new(records[0].Seq, 4, "original failure", true, false), Token);
            failed = Assert.Single(queue.ReadFailed(1, 0, 10));
            pending = Assert.Single(queue.ReadPending(1, 0, 10));
            setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) });
        using var http = running.CreateClient();
        var cache = running.Services.GetRequiredService<DeliveryCache>();
        Assert.Equal(2, cache.ReadQuarantined(1, 0, 10).Count);
        await clock.PollAsync();
        var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
        var row = store.ReadIntent(intent.Hold.IntentId)!;
        Assert.Equal("completed", row.Status);
        Assert.Equal(2, store.Read(Guid.Parse(row.LastOperationId!))!.AffectedRecords);
        var retained = Assert.Single(cache.ReadFailed(1, 0, 10));
        Assert.Equal((failed.Seq, failed.DeliveryId, failed.CreatedAt, failed.FailedAt, 0),
            (retained.Seq, retained.DeliveryId, retained.CreatedAt, retained.FailedAt, retained.FailureCount));
        Assert.Equal(failed.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(intent.Target.Fingerprint, retained.Fingerprint);
        Assert.Equal(pending.Seq, Assert.Single(cache.ReadPending(1, 0, 10)).Seq);
        Assert.Empty(received);
        var controls = running.Services.GetRequiredService<OutputControlStore>();
        var pause = controls.Read(1);
        Assert.True(pause.ManuallyPaused);
        controls.Write(1, new(false, true, pause.ResumeVersion), "admin", null);
        await OutputPipelineTests.UntilAsync(() => received.Count == 1 && cache.ReadPending(1, 0, 10).Count == 0);
        Assert.Equal((Encoding.UTF8.GetString(pending.Payload.Span), pending.DeliveryId.ToString("D")), Assert.Single(received));
        Assert.Single(cache.ReadFailed(1, 0, 10));
        Assert.Single(TestAccounts.Audit(running.Services, "delivery.migrationStarted"));
        Assert.Single(TestAccounts.Audit(running.Services, "delivery.migrationFinished"));
        Assert.Equal(1, running.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<RuntimeEventRecord>()
            .Where(value => value.Type == "delivery.migrationIntentCompleted").Count());
        await peer.StopAsync(Token);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("account")]
    [InlineData("quota")]
    public async Task ChangedRecipientOrQuotaRejectsEntireIntentAndDoesNotAutomaticallyRetry(string change)
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationIntentDraft intent;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            intent = await MigrationIntentFixture.RegisterAsync(setup.Services);
            await setup.Services.GetRequiredService<RuntimeCoordinator>().Runtime.HoldMigrationAsync(intent.Hold, null, Token);
            if (change == "address")
            {
                var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
                versions.Commit(versions.FreezeDraft(MigrationIntentFixture.Plan("http://third.test/base/"), "0", "admin"), MigrationIntentFixture.Application);
            }
            if (change == "account")
            {
                await setup.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(1,
                    new(intent.TargetRecipient, HttpCredentialKind.Basic, Username: "different-owner", Secret: "test-only"), "admin", null, true, token: TestContext.Current.CancellationToken);
            }
            setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock, configure: services =>
        {
            if (change == "quota")
            {
                services.AddSingleton(provider => new DeliveryCache(new DeliveryCacheOptions
                {
                    DatabasePath = Path.Combine(data.Path, DeliveryCacheOptions.FileName),
                    ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
                    MaximumPendingPerTarget = 1,
                }, TimeProvider.System));
            }
        });
        using var http = running.CreateClient();
        await clock.PollAsync();
        var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
        var row = store.ReadIntent(intent.Hold.IntentId)!;
        Assert.Equal("rejected", row.Status);
        Assert.Equal(change == "quota" ? DeliveryMigrationErrors.QuotaExceeded : DeliveryMigrationErrors.TargetChanged,
            store.Read(Guid.Parse(row.LastOperationId!))!.ErrorCode);
        var cache = running.Services.GetRequiredService<DeliveryCache>();
        Assert.Equal(2, cache.ReadQuarantined(1, 0, 10).Count);
        Assert.Empty(cache.ReadPending(1, 0, 10));
        Assert.Null(cache.ReadMigrationReceipt(Guid.Parse(row.LastOperationId!)));
        await clock.PollAsync();
        await clock.PollAsync();
        Assert.Single(TestAccounts.Audit(running.Services, "delivery.migrationStarted"));
        Assert.Equal(1, running.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<RuntimeEventRecord>()
            .Where(value => value.Type == "delivery.migrationIntentRejected").Count());
        Assert.False(running.Services.GetRequiredService<RuntimeCoordinator>().Status.QueueOperationInProgress);
    }

    [Fact]
    public async Task UnavailableTargetKeepsIntentWaitingWithoutHoldingApplicationMutexOrStoppingStabilityClock()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationIntentDraft intent;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            intent = await MigrationIntentFixture.RegisterAsync(setup.Services);
            await setup.Services.GetRequiredService<RuntimeCoordinator>().Runtime.HoldMigrationAsync(intent.Hold, null, Token);
            var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(RuntimePlanDocument.Parse("{\"schemaVersion\":1,\"units\":[]}"u8), "0", "admin"), MigrationIntentFixture.Application);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock, new Dictionary<string, string> { [RuntimeSettings.StableAfterSecondsKey] = "1" });
        using var http = running.CreateClient();
        await clock.PollAsync();
        var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
        Assert.Equal("waiting", store.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Null(store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
        var versionsNow = running.Services.GetRequiredService<RuntimeVersionStore>();
        await OutputPipelineTests.UntilAsync(() => versionsNow.Load(3)!.IsStable);
        var coordinator = running.Services.GetRequiredService<RuntimeCoordinator>();
        var draft = running.Services.GetRequiredService<RouteDraftStore>().Read();
        Assert.True((await coordinator.ApplyDraftAsync(draft.Version, "admin", null, Token)).Activated);
        Assert.False(coordinator.Status.QueueOperationInProgress);
        Assert.Empty(TestAccounts.Audit(running.Services, "delivery.migrationStarted"));
    }

    [Fact]
    public async Task FailedResultSynchronizationNeverReexecutesAndRestartUsesDurableReceipt()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationIntentDraft intent;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            intent = await MigrationIntentFixture.RegisterAsync(setup.Services);
            setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
            setup.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("CREATE TRIGGER fail_finish BEFORE INSERT ON audit_log WHEN NEW.action='delivery.migrationFinished' BEGIN SELECT abs(-9223372036854775808); END;");
        }
        Guid operation;
        var clock = new MigrationPollClock();
        await using (var running = Factory(data.Path, clock))
        {
            using var http = running.CreateClient();
            await clock.PollAsync();
            var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
            var row = store.ReadIntent(intent.Hold.IntentId)!;
            operation = Guid.Parse(row.LastOperationId!);
            Assert.Equal("waiting", row.Status);
            Assert.Equal("started", store.Read(operation)!.Status);
            Assert.Equal(2, running.Services.GetRequiredService<DeliveryCache>().ReadMigrationReceipt(operation)!.AffectedRecords);
            await clock.PollAsync();
            await clock.PollAsync();
            Assert.Single(TestAccounts.Audit(running.Services, "delivery.migrationStarted"));
            Assert.Empty(TestAccounts.Audit(running.Services, "delivery.migrationFinished"));
            running.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("DROP TRIGGER fail_finish");
        }
        var restartedClock = new MigrationPollClock();
        await using var restarted = Factory(data.Path, restartedClock);
        using var clientAgain = restarted.CreateClient();
        var final = restarted.Services.GetRequiredService<DeliveryMigrationStore>();
        Assert.Equal("completed", final.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Equal("completed", final.Read(operation)!.Status);
        await restartedClock.PollAsync();
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationStarted"));
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationFinished"));
        Assert.Equal(2, restarted.Services.GetRequiredService<DeliveryCache>().GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MqttUsesEffectiveSenderIdentityAndWaitsWhenCredentialIdentityIsUnavailable(bool missingCredential)
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationIntentDraft intent;
        RuntimePlanDocument Plan(string host) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new[] { new { kind = "output.mqtt", key = "queue", configuration = new { connectionId = 1, host,
                queueOnly = true, topic = "", inputs = Array.Empty<string>(), usePassword = missingCredential } } },
        }));
        OutputTargetIdentity Identity(string host)
        {
            var settings = new MqttConnectionSettings { ConnectionId = 1, Host = host, UsePassword = missingCredential };
            var credential = missingCredential ? new MqttCredentialIdentity("owner", "historical-account-marker", "old-version") : null;
            return new(settings.Fingerprint(credential?.Username, credential?.AccountMarker),
                OutputRecipientIdentity.Mqtt(settings.Recipient, missingCredential, credential));
        }
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.Freeze(Plan("old.test"), RuntimeSnapshotOrigins.Seed, null, "system"), null);
            var old = Identity("old.test");
            var next = Identity("new.test");
            intent = new(new(setup.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), 1, old.Fingerprint),
                old.Recipient, next.Recipient.Address, true, next);
            Assert.True((await setup.Services.GetRequiredService<DeliveryCache>().EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
                "mqtt-frozen"u8.ToArray(), old.Fingerprint, old.Summary, "frozen/topic", 1)], Token)).Committed);
            versions.Commit(versions.FreezeDraft(Plan("new.test"), "0", "admin"), MigrationIntentFixture.Application, markRunning: true, migrationIntents: [intent]);
            versions.ClearRunningMarker(2);
            setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock);
        using var http = running.CreateClient();
        await clock.PollAsync();
        var row = running.Services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intent.Hold.IntentId)!;
        Assert.Equal(missingCredential ? "waiting" : "completed", row.Status);
        var queue = running.Services.GetRequiredService<DeliveryCache>();
        if (missingCredential)
        {
            Assert.Null(row.LastOperationId);
            Assert.Single(queue.ReadQuarantined(1, 0, 10));
        }
        else
        {
            var pending = Assert.Single(queue.ReadPending(1, 0, 10));
            Assert.Equal(intent.Target.Fingerprint, pending.Fingerprint);
            Assert.Equal("frozen/topic", pending.TargetAddress);
            Assert.Equal(1, pending.QualityOfService);
        }
        Assert.True(running.Services.GetRequiredService<OutputControlStore>().Read(1).ManuallyPaused);
    }

    [Fact]
    public async Task ConsecutiveAddressChangesRejectSupersededIntentBeforeExecutingNextAndNeverMixScopes()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationIntentDraft first, second;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            first = await MigrationIntentFixture.RegisterAsync(setup.Services);
            var runtime = setup.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
            await runtime.HoldMigrationAsync(first.Hold, null, Token);
            second = MigrationIntentFixture.Intent(setup.Services, first.TargetRecipient, "http://third.test/base/");
            Assert.True((await runtime.Deliveries.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 2, "second-intent"u8.ToArray(),
                second.Hold.OldFingerprint, second.Source.Freeze(), "send")], Token)).Committed);
            var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(MigrationIntentFixture.Plan(second.TargetRecipient), "0", "admin"), MigrationIntentFixture.Application,
                markRunning: true, migrationIntents: [second]);
            versions.ClearRunningMarker(3);
            setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock);
        using var http = running.CreateClient();
        var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
        var queue = running.Services.GetRequiredService<DeliveryCache>();
        Assert.Equal(3, queue.ReadQuarantined(1, 0, 10).Count);
        await clock.PollAsync();
        Assert.Equal("rejected", store.ReadIntent(first.Hold.IntentId)!.Status);
        Assert.Null(store.ReadIntent(second.Hold.IntentId)!.LastOperationId);
        await clock.PollAsync();
        Assert.Equal("completed", store.ReadIntent(second.Hold.IntentId)!.Status);
        Assert.Equal(second.Target.Fingerprint, Assert.Single(queue.ReadPending(1, 0, 10)).Fingerprint);
        Assert.All(queue.ReadQuarantined(1, 0, 10), row => Assert.Equal(first.Hold.OldFingerprint, row.Fingerprint));
        Assert.Equal(2, queue.ReadQuarantined(1, 0, 10).Count);
        Assert.Equal(1, queue.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.Equal(2, TestAccounts.Audit(running.Services, "delivery.migrationFinished").Count);
    }

    [Fact]
    public async Task OwnedExecutorWaitsForInflightAckAndStopWaitsForItsDurableResult()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MapPost("/new/send", async context =>
        {
            sending.TrySetResult();
            await release.Task;
            context.Response.StatusCode = 204;
        });
        await peer.StartAsync(Token);
        using var data = TemporaryDirectory.Create();
        var address = new Uri(peer.Urls.Single());
        DeliveryMigrationIntentDraft intent;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            intent = await MigrationIntentFixture.RegisterAsync(setup.Services, new Uri(address, "/new/").AbsoluteUri);
        }
        var clock = new MigrationPollClock();
        await using var running = Factory(data.Path, clock, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) });
        using var http = running.CreateClient();
        var coordinator = running.Services.GetRequiredService<RuntimeCoordinator>();
        var queue = coordinator.Runtime.Deliveries;
        try
        {
            var id = Guid.NewGuid();
            Assert.True((await queue.EnqueueAsync([new(id, 1, Guid.NewGuid(), "meter", 2, "current"u8.ToArray(),
                intent.Target.Fingerprint, intent.Target.Summary, "send", headers: new Dictionary<string, string> { ["Idempotency-Key"] = id.ToString("D") },
                protocolMetadata: TenonEdge.Adapters.Http.HttpFrozenRequest.Freeze(new() { Path = "send" }))], Token)).Committed);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await clock.FireAsync();
            var store = running.Services.GetRequiredService<DeliveryMigrationStore>();
            await OutputPipelineTests.UntilAsync(() => store.ReadIntent(intent.Hold.IntentId)!.LastOperationId is not null);
            var operation = Guid.Parse(store.ReadIntent(intent.Hold.IntentId)!.LastOperationId!);
            Assert.True(coordinator.Status.QueueOperationInProgress);
            Assert.Null(queue.ReadMigrationReceipt(operation));
            var draft = running.Services.GetRequiredService<RouteDraftStore>().Read();
            await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync(draft.Version, "admin", null, Token));
            var stopping = coordinator.StopAsync(Token);
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.Equal("completed", store.ReadIntent(intent.Hold.IntentId)!.Status);
            Assert.Equal(2, queue.ReadMigrationReceipt(operation)!.AffectedRecords); // Current in-flight ACK was deleted, not migrated.
            Assert.Single(TestAccounts.Audit(running.Services, "delivery.migrationFinished"));
            Assert.False(coordinator.Status.QueueOperationInProgress);
        }
        finally { release.TrySetResult(); await peer.StopAsync(CancellationToken.None); }
    }

    private static FixedDataDirectoryApplicationFactory Factory(string directory, MigrationPollClock clock,
        IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null) => new(directory, settings, services =>
        {
            // Only coordinator timers are controlled. Queue commits, sender deadlines and the host retain real time.
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<RuntimeCoordinator>(provider, clock));
            configure?.Invoke(services);
        });


}
