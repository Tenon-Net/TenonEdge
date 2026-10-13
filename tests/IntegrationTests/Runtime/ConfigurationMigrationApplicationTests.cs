using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationMigrationApplicationTests
{
    private const string Root = "/api/edge-admin/v1/configuration/migrations";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminReviewApplyIsolationAndExecutorAreOneRealProductFlowForBothProtocols(bool mqtt)
    {
        using var data = TemporaryDirectory.Create();
        var clock = new MigrationPollClock();
        Guid intentId;
        await using (var factory = Factory(data.Path, clock))
        {
            var saved = await Seed(factory, mqtt);
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            var cache = factory.Services.GetRequiredService<DeliveryCache>();
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            var original = cache.ReadPending(saved.Id, 0, 10).Concat(cache.ReadFailed(saved.Id, 0, 10)).OrderBy(row => row.Seq).ToArray();
            using var prepared = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id]), Token);
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            Assert.True(prepared.Headers.CacheControl!.NoStore);
            var review = await prepared.ReadJsonAsync(Token);
            var id = review.GetProperty("id").GetGuid();
            var target = Assert.Single(review.GetProperty("targets").EnumerateArray());
            intentId = target.GetProperty("intentId").GetGuid();
            Assert.True(review.GetProperty("canConfirm").GetBoolean());
            Assert.Equal(2, target.GetProperty("count").GetInt32());
            Assert.Equal(mqtt ? "mqtt" : "http", target.GetProperty("source").GetProperty("protocol").GetString());
            Assert.Contains("old.test", target.GetProperty("source").GetProperty("address").GetString()!, StringComparison.Ordinal);
            Assert.Contains("new.test", target.GetProperty("target").GetProperty("address").GetString()!, StringComparison.Ordinal);
            Assert.False(review.GetProperty("configuration").GetProperty("preflightPassed").GetBoolean());
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            Assert.Null(versions.ReadPointers().PendingRevision);
            Assert.Null(cache.ReadMigrationHold(intentId));
            Assert.Null(factory.Services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intentId));
            using var confirmed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true, "address ownership reviewed"), Token);
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
            Assert.Equal(id, (await confirmed.ReadJsonAsync(Token)).GetProperty("id").GetGuid());
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            Assert.Equal(RuntimeApplicationStage.Completed, coordinator.ReadApplication(id)!.Stage);
            Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
            Assert.Equal("address ownership reviewed", versions.Load(2)!.ChangeDescription);
            Assert.Equal(2, cache.ReadMigrationHold(intentId)!.HeldRecords);
            Assert.Equal(2, cache.ReadQuarantined(saved.Id, 0, 10).Count);
            Assert.Empty(cache.ReadPending(saved.Id, 0, 10));
            Assert.Empty(cache.ReadFailed(saved.Id, 0, 10));
            var intent = await admin.GetJsonAsync($"{Root}/intents/{intentId}", Token);
            Assert.Equal("waiting", intent.GetProperty("status").GetString());
            using var repeated = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
            Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
            await clock.PollAsync();
            var result = await admin.GetJsonAsync($"{Root}/intents/{intentId}", Token);
            Assert.Equal("completed", result.GetProperty("status").GetString());
            Assert.Equal(2, result.GetProperty("affectedRecords").GetInt32());
            var after = cache.ReadPending(saved.Id, 0, 10).Concat(cache.ReadFailed(saved.Id, 0, 10)).OrderBy(row => row.Seq).ToArray();
            Assert.Equal(original.Length, after.Length);
            for (var i = 0; i < original.Length; i++)
            {
                Assert.Equal((original[i].Seq, original[i].DeliveryId, original[i].State, original[i].CreatedAt, original[i].FailedAt, original[i].TargetAddress, original[i].QualityOfService),
                    (after[i].Seq, after[i].DeliveryId, after[i].State, after[i].CreatedAt, after[i].FailedAt, after[i].TargetAddress, after[i].QualityOfService));
                Assert.Equal(original[i].Payload.ToArray(), after[i].Payload.ToArray());
            }
            Assert.True(factory.Services.GetRequiredService<OutputControlStore>().Read(saved.Id).ManuallyPaused);
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationFinished"));
        }
        await using var restarted = Factory(data.Path, new MigrationPollClock());
        using var next = await restarted.CreateLoggedInClientAsync("admin", Token);
        Assert.Equal("completed", (await next.GetJsonAsync($"{Root}/intents/{intentId}", Token)).GetProperty("status").GetString());
        Assert.Equal(2, restarted.Services.GetRequiredService<DeliveryCache>().GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("viewer")]
    [InlineData("configurator")]
    public async Task AllMigrationApplicationEndpointsAreAdminOnlyAndOrdinaryApplyStillCannotBypassProtection(string role)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock());
        var saved = await Seed(factory);
        using var client = role == "anonymous" ? factory.CreateClient() : await factory.CreateLoggedInClientAsync(role, Token);
        var expected = role == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        using var prepared = await client.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id]), Token);
        using var current = await client.GetAsync(Root + "/preparation", Token);
        using var cancel = await client.DeleteAsync($"{Root}/preparation/{Guid.NewGuid()}", Token);
        using var confirm = await client.PostJsonAsync($"{Root}/{Guid.NewGuid()}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        using var intent = await client.GetAsync($"{Root}/intents/{Guid.NewGuid()}", Token);
        foreach (var response in new[] { prepared, current, cancel, confirm, intent })
        { Assert.Equal(expected, response.StatusCode); }
        if (role == "configurator")
        {
            var review = factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token);
            using var ordinary = await client.PostJsonAsync("/api/edge-admin/v1/configuration/apply",
                new ConfigurationApplicationRequest(saved.Version, review.EffectiveRevision, review.PlanHash!, true), Token);
            Assert.Equal(HttpStatusCode.Conflict, ordinary.StatusCode);
        }
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
    }

    [Fact]
    public async Task SessionOwnerCsrfExplicitConfirmationAndCancellationProtectTheOneShotReview()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock());
        var saved = await Seed(factory);
        using var first = await factory.CreateLoggedInClientAsync("admin", Token);
        using var second = await factory.CreateLoggedInClientAsync("admin", Token);
        var review = await Prepare(first, saved);
        var id = review.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, (await second.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").ValueKind);
        using var foreign = await second.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var busy = await second.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id]), Token);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        foreach (var request in new[] { new ConfigurationMigrationConfirmation(false, true), new ConfigurationMigrationConfirmation(true, false) })
        {
            using var invalid = await first.PostJsonAsync($"{Root}/{id}/confirm", request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var csrf = first.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        first.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var missingCsrf = await first.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        first.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        Assert.Equal(id, (await first.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        using var cancelled = await first.DeleteAsync($"{Root}/preparation/{id}", Token);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var replay = await first.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaInsufficientAtPreparationOrAtFinalCheckNeverCommitsAnIntent(bool fillsLater)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock(), services => services.AddSingleton(provider => new DeliveryCache(new()
        {
            DatabasePath = Path.Combine(data.Path, DeliveryCacheOptions.FileName),
            ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            MaximumPendingPerTarget = 1,
        }, TimeProvider.System)));
        var saved = await Seed(factory, count: fillsLater ? 1 : 2);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var review = await Prepare(admin, saved);
        Assert.Equal(fillsLater, review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(fillsLater ? 0 : 1, review.GetProperty("targets")[0].GetProperty("quota").GetProperty("pendingRecords").GetInt32());
        var id = review.GetProperty("id").GetGuid();
        if (fillsLater)
        {
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var identity = coordinator.Runtime.CaptureMigrationTarget(saved.Id, 1)!;
            Assert.True((await coordinator.Runtime.Deliveries.EnqueueAsync([Draft(saved.Id, identity.Fingerprint, identity.Summary)], Token)).Committed);
        }
        using var confirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        if (fillsLater)
        {
            Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            Assert.Equal(DeliveryMigrationErrors.QuotaExceeded, coordinator.ReadApplication(id)!.ErrorCode);
            Assert.False(coordinator.ReadApplication(id)!.Committed);
        }
        else
        { Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode); }
        Assert.Equal(1, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("credentials")]
    public async Task ChangedDraftOrSourceIdentityConsumesReviewWithoutCommittingOrAllocatingARevision(string change)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock());
        var saved = await Seed(factory);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var review = await Prepare(admin, saved);
        var id = review.GetProperty("id").GetGuid();
        if (change == "draft")
        {
            factory.Services.GetRequiredService<ConnectionDraftStore>().Write(saved.Id,
                new(saved.Version, "source", "Changed name", ConnectionKind.Http, Http: new("http://new.test/base/")), "admin", null);
        }
        else
        {
            factory.Services.GetRequiredService<HttpCredentialStore>().Write(saved.Id,
                new("http://new.test/base/", HttpCredentialKind.Basic, Username: "different-owner", Secret: "test-only", ExpectedDraftVersion: saved.Version), "admin", null, true);
        }
        using var changed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var consumed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.NotFound, consumed.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
    }

    [Fact]
    public async Task ConfirmationLifetimeIsMonotonicAndExpiryCannotApplyConfiguration()
    {
        using var data = TemporaryDirectory.Create();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, new MigrationPollClock(), services =>
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ConfigurationMigrationPreparation>(provider, time)));
        var saved = await Seed(factory);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var id = (await Prepare(admin, saved)).GetProperty("id").GetGuid();
        time.StepWallClock(TimeSpan.FromDays(5));
        Assert.Equal(id, (await admin.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        time.Advance(ConfigurationMigrationPreparation.Lifetime);
        using var expired = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").ValueKind);
        Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Fact]
    public async Task SelectionAndRequestBoundariesRejectUnknownDuplicateUnsafeAndSpoofedInputs()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock());
        var saved = await Seed(factory);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var ids in new long[][] { [], [saved.Id, saved.Id], [0], [9007199254740992L], Enumerable.Range(1, 501).Select(id => (long)id).ToArray() })
        {
            using var invalid = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, ids), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var unknown = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id + 1]), Token);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        using var spoofed = await admin.PostJsonAsync(Root + "/prepare", new { expectedVersion = saved.Version, connectionIds = new[] { saved.Id }, oldFingerprint = "forged", account = "forged" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        var id = (await Prepare(admin, saved)).GetProperty("id").GetGuid();
        using var spoofedConfirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = true, sameOwnerConfirmed = true, targetFingerprint = "forged" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, spoofedConfirm.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommitAuditFailureRestoresOldVersionButPostCommitIsolationFailureKeepsFenceUntilRecovery(bool afterCommit, bool recoverWithApply)
    {
        using var data = TemporaryDirectory.Create();
        var clock = new MigrationPollClock();
        await using var factory = Factory(data.Path, clock);
        var saved = await Seed(factory);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var review = await Prepare(admin, saved);
        var id = review.GetProperty("id").GetGuid();
        var intentId = review.GetProperty("targets")[0].GetProperty("intentId").GetGuid();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        if (afterCommit)
        { QueueSql(data.Path, "CREATE TRIGGER fail_hold BEFORE INSERT ON delivery_migration_hold BEGIN SELECT abs(-9223372036854775808); END;"); }
        else
        { database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_intent BEFORE INSERT ON audit_log WHEN NEW.action='delivery.migrationIntentRegistered' BEGIN SELECT abs(-9223372036854775808); END;"); }
        using var confirmed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
        Assert.Equal(RuntimeApplicationStage.Failed, coordinator.ReadApplication(id)!.Stage);
        Assert.Equal(afterCommit, coordinator.ReadApplication(id)!.Committed);
        Assert.Equal(afterCommit ? 2 : 1, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Null(coordinator.Runtime.Deliveries.ReadMigrationHold(intentId));
        Assert.Single(coordinator.Runtime.Deliveries.ReadPending(saved.Id, 0, 10));
        Assert.Single(coordinator.Runtime.Deliveries.ReadFailed(saved.Id, 0, 10));
        if (!afterCommit)
        {
            Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
            Assert.Null(factory.Services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intentId));
            Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
            database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_intent");
            return;
        }
        Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
        Assert.Equal(1, coordinator.Runtime.ActiveRevision); // No handover while old records are not durably held.
        await clock.PollAsync();
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationStarted"));
        QueueSql(data.Path, "DROP TRIGGER fail_hold");
        var state = await admin.GetJsonAsync("/api/edge-admin/v1/configuration/runtime/", Token);
        Assert.True(state.GetProperty("canRetry").GetBoolean());
        var recoveryReview = factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token);
        using var retry = recoverWithApply
            ? await admin.PostJsonAsync("/api/edge-admin/v1/configuration/apply", new ConfigurationApplicationRequest(saved.Version,
                recoveryReview.EffectiveRevision, recoveryReview.PlanHash!, true), Token)
            : await admin.PostJsonAsync("/api/edge-admin/v1/configuration/runtime/retry",
                new ConfigurationRetryRequest(2, state.GetProperty("planHash").GetString()!, true), Token);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryId = (await retry.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(retryId)?.FinishedAt is not null);
        Assert.Equal(RuntimeApplicationStage.Completed, coordinator.ReadApplication(retryId)!.Stage);
        Assert.Equal(2, coordinator.Runtime.Deliveries.ReadMigrationHold(intentId)!.HeldRecords);
        Assert.Equal(recoverWithApply ? 3 : 2, coordinator.Runtime.ActiveRevision);
        await clock.PollAsync();
        Assert.Equal("completed", factory.Services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intentId)!.Status);
    }

    [Fact]
    public async Task RealHttpAckPrecedesCommitAndLostConfirmationContextDoesNotReleaseTheOwnedApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        ConcurrentQueue<(string Path, string Body, string Key)> received = new();
        var oldSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MapPost("/{address}/send", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            received.Enqueue((context.Request.Path.ToString(), await reader.ReadToEndAsync(Token), context.Request.Headers["Idempotency-Key"].ToString()));
            if (context.Request.Path == "/old/send")
            { oldSending.TrySetResult(); await release.Task; }
            context.Response.StatusCode = 204;
        });
        await peer.StartAsync(Token);
        var address = new Uri(peer.Urls.Single());
        using var data = TemporaryDirectory.Create();
        var clock = new MigrationPollClock();
        await using var factory = Factory(data.Path, clock, settings: new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) });
        try
        {
            var saved = await Seed(factory, count: 3, oldAddress: new Uri(address, "/old/").AbsoluteUri, newAddress: new Uri(address, "/new/").AbsoluteUri, sendTimeoutMs: 60000);
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var cache = coordinator.Runtime.Deliveries;
            var original = cache.ReadPending(saved.Id, 0, 10);
            var controls = factory.Services.GetRequiredService<OutputControlStore>();
            controls.Write(saved.Id, new(false, true, controls.Read(saved.Id).ResumeVersion), "admin", null);
            await oldSending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Guid id, intentId;
            using (var admin = await factory.CreateLoggedInClientAsync("admin", Token))
            {
                var review = await Prepare(admin, saved);
                id = review.GetProperty("id").GetGuid();
                intentId = review.GetProperty("targets")[0].GetProperty("intentId").GetGuid();
                using var confirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
                Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
            } // The HTTP client/request context has ended; the original source request is still in flight.
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.Stage == RuntimeApplicationStage.Quiescing);
            Assert.Equal(1, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
            Assert.Null(cache.ReadMigrationHold(intentId));
            Assert.Single(received);
            release.TrySetResult();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            Assert.Equal(RuntimeApplicationStage.Completed, coordinator.ReadApplication(id)!.Stage);
            Assert.Equal(2, cache.ReadMigrationHold(intentId)!.HeldRecords); // One original pending ACK, one remaining pending, one original failed.
            Assert.Single(received);
            await clock.PollAsync();
            await OutputPipelineTests.UntilAsync(() => received.Count == 2 && cache.ReadPending(saved.Id, 0, 10).Count == 0);
            var requests = received.ToArray();
            Assert.Equal(("/old/send", original[0].DeliveryId.ToString("D")), (requests[0].Path, requests[0].Key));
            Assert.Equal(("/new/send", original[1].DeliveryId.ToString("D")), (requests[1].Path, requests[1].Key));
            Assert.Equal(System.Text.Encoding.UTF8.GetString(original[1].Payload.Span), requests[1].Body);
            Assert.Single(cache.ReadFailed(saved.Id, 0, 10));
            using var readerClient = await factory.CreateLoggedInClientAsync("admin", Token);
            Assert.Equal("completed", (await readerClient.GetJsonAsync($"{Root}/intents/{intentId}", Token)).GetProperty("status").GetString());
            Assert.Equal(2, cache.GetPersistentCounters()["manuallyMigratedRecords"]);
        }
        finally { release.TrySetResult(); await peer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task UnselectedBlockedConnectionCannotBeSmuggledIntoApplicationAndSelectingBothRegistersSeparateIntents()
    {
        using var data = TemporaryDirectory.Create();
        var clock = new MigrationPollClock();
        await using var factory = Factory(data.Path, clock);
        var saved = await Seed(factory);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var reverted = connections.Write(saved.Id, new(saved.Version, "source", "Source", ConnectionKind.Http, Http: new("http://old.test/base/")), "admin", null);
        var second = connections.Write(null, new(reverted.Version, "second", "Second", ConnectionKind.Http, Http: new("http://old-two.test/")), "admin", null);
        factory.Services.GetRequiredService<OutputControlStore>().Write(second.Connection.Id, new(true, true), "admin", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(second.Version, "admin", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders().All(sender => sender.Sender.State == "manuallyPaused"));
        var identity = coordinator.Runtime.CaptureMigrationTarget(second.Connection.Id, 2)!;
        await coordinator.Runtime.Deliveries.EnqueueAsync([Draft(second.Connection.Id, identity.Fingerprint, identity.Summary)], Token);
        var firstChanged = connections.Write(saved.Id, new(second.Version, "source", "Source", ConnectionKind.Http, Http: new("http://new.test/base/")), "admin", null);
        var both = connections.Write(second.Connection.Id, new(firstChanged.Version, "second", "Second", ConnectionKind.Http, Http: new("http://new-two.test/")), "admin", null);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var incomplete = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(both.Version, [saved.Id]), Token);
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        Assert.Equal(2, factory.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered"));
        using var prepared = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(both.Version, [saved.Id, second.Connection.Id]), Token);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var review = await prepared.ReadJsonAsync(Token);
        Assert.Equal(2, review.GetProperty("targets").GetArrayLength());
        var id = review.GetProperty("id").GetGuid();
        using var confirmed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
        Assert.Equal(RuntimeApplicationStage.Completed, coordinator.ReadApplication(id)!.Stage);
        Assert.Equal(2, TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered").Count);
        await clock.PollAsync();
        await clock.PollAsync();
        foreach (var target in review.GetProperty("targets").EnumerateArray())
        {
            var outcome = await admin.GetJsonAsync($"{Root}/intents/{target.GetProperty("intentId").GetGuid()}", Token);
            Assert.Equal("completed", outcome.GetProperty("status").GetString());
        }
        Assert.Equal(3, coordinator.Runtime.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Fact]
    public async Task PreparationAndConfirmationSharePreviewBudget()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, new MigrationPollClock());
        var saved = await Seed(factory);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var review = await Prepare(admin, saved);
        var id = review.GetProperty("id").GetGuid();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(admin.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using (budget.TryAcquire(key)!)
        using (budget.TryAcquire(key)!)
        {
            using var deniedPrepare = await admin.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id]), Token);
            using var deniedConfirm = await admin.PostJsonAsync($"{Root}/{id}/confirm", new ConfigurationMigrationConfirmation(true, true), Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, deniedPrepare.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, deniedConfirm.StatusCode);
        }
        Assert.Equal(id, (await admin.GetJsonAsync(Root + "/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Fact]
    public async Task RealKestrelEnforcesMigrationBodyLimitBeforeModelBinding()
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
        foreach (var path in new[] { Root + "/prepare", $"{Root}/{Guid.NewGuid()}/confirm" })
        {
            using var body = new StringContent(new string(' ', 32 * 1024) + "{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(path, UriKind.Relative), body, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.RequestEntityTooLarge, Token);
        }
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

    private static void QueueSql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(path, DeliveryCacheOptions.FileName)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<JsonElement> Prepare(HttpClient client, Saved saved)
    {
        using var response = await client.PostJsonAsync(Root + "/prepare", new ConfigurationMigrationPrepareRequest(saved.Version, [saved.Id]), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(Token);
    }
    private static FixedDataDirectoryApplicationFactory Factory(string data, MigrationPollClock clock, Action<IServiceCollection>? configure = null,
        IReadOnlyDictionary<string, string>? settings = null) => new(data, settings,
        configureServices: services => { services.AddSingleton(provider => ActivatorUtilities.CreateInstance<RuntimeCoordinator>(provider, clock)); configure?.Invoke(services); });
    private static DeliveryDraft Draft(long id, string fingerprint, string summary, bool mqtt = false)
    {
        var delivery = Guid.NewGuid();
        return new(delivery, id, Guid.NewGuid(), "meter", 1, "{\"original\":42}"u8.ToArray(), fingerprint, summary, mqtt ? "frozen/topic" : "send", mqtt ? 1 : null,
            new Dictionary<string, string> { ["Idempotency-Key"] = delivery.ToString("D") }, protocolMetadata: mqtt ? null : HttpFrozenRequest.Freeze(new() { Path = "send" }));
    }
    private static async Task<Saved> Seed(FixedDataDirectoryApplicationFactory factory, bool mqtt = false, int count = 2,
        string oldAddress = "http://old.test/base/", string newAddress = "http://new.test/base/", int sendTimeoutMs = 5000)
    {
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var before = connections.Write(null, new("0", "source", "Source", mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
            Mqtt: mqtt ? new("old.test") : null, Http: mqtt ? null : new(oldAddress, sendTimeoutMs)), "admin", null);
        factory.Services.GetRequiredService<OutputControlStore>().Write(before.Connection.Id, new(true, true), "admin", null);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(before.Version, "admin", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders().SingleOrDefault()?.Sender.State == "manuallyPaused");
        var identity = coordinator.Runtime.CaptureMigrationTarget(before.Connection.Id, 1)!;
        var cache = coordinator.Runtime.Deliveries;
        for (var i = 0; i < count; i++)
        {
            var added = await cache.EnqueueAsync([Draft(before.Connection.Id, identity.Fingerprint, identity.Summary, mqtt)], Token);
            Assert.True(added.Committed);
            if (i == 0)
            { await cache.RecordFailureAsync(new(added.Seqs.Single(), 4, "original-failure", true, false), Token); }
        }
        var next = connections.Write(before.Connection.Id, new(before.Version, "source", "Source", mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
            Mqtt: mqtt ? new("new.test") : null, Http: mqtt ? null : new(newAddress, sendTimeoutMs)), "admin", null);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "configurator", ApiRequests.StrongPassword, UserRole.Configurator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        return new(next.Connection.Id, next.Version);
    }
    private sealed record Saved(long Id, string Version);
}
