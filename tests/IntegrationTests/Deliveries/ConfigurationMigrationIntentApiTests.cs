using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class ConfigurationMigrationIntentApiTests
{
    private const string Root = "/api/edge-admin/v1/configuration/migrations";
    private const string CurrentAddress = "http://current.test/base/";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitApprovalPreservesFrozenRangeHistoryOriginalRegistrantAndPause(bool legacy, bool mqtt)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path, legacy, mqtt: mqtt);
        var currentAddress = mqtt ? new MqttOutputSettings { Host = "current.test" }.Recipient : CurrentAddress;
        Guid attempt;
        await using (var factory = Running(data.Path, out var clock))
        {
            using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
            await clock.PollAsync();
            var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
            var cache = factory.Services.GetRequiredService<DeliveryCache>();
            var previous = store.ReadIntent(intent.Hold.IntentId)!.LastOperationId;
            Assert.Equal(legacy ? "waiting" : "rejected", store.ReadIntent(intent.Hold.IntentId)!.Status);
            var originals = cache.ReadQuarantined(1, 0, 10);
            Assert.Equal(2, originals.Count);
            var inspect = await admin.GetJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
            Assert.True(inspect.GetProperty("canPrepareRetry").GetBoolean());
            Assert.False(inspect.GetProperty("targetMatchesConfirmation").GetBoolean());
            Assert.Equal(currentAddress, inspect.GetProperty("currentTarget").GetProperty("address").GetString());
            Assert.Equal(intent.Source.Address, inspect.GetProperty("source").GetProperty("address").GetString());
            if (legacy)
            { Assert.Equal(System.Text.Json.JsonValueKind.Null, inspect.GetProperty("approvedTarget").ValueKind); }
            else
            { Assert.Equal(intent.TargetRecipient, inspect.GetProperty("approvedTarget").GetProperty("address").GetString()); }
            Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
            attempt = await Prepare(admin, intent.Hold.IntentId);
            Assert.Equal(previous, store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
            using var response = await Confirm(admin, attempt);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, (await response.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
            var row = store.ReadIntent(intent.Hold.IntentId)!;
            Assert.Equal(("completed", currentAddress, "admin"), (row.Status, row.TargetRecipient, row.AccountName));
            Assert.Equal(attempt.ToString("D"), row.LastOperationId);
            Assert.Equal("operator", store.Read(attempt)!.AccountName);
            if (previous is not null)
            { Assert.Equal("rejected", store.Read(Guid.Parse(previous))!.Status); }
            Assert.Equal(legacy ? 1 : 2, factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<DeliveryMigrationRecord>()
                .Where(record => record.IntentId == row.IntentId).Count());
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
            var pending = cache.ReadPending(1, 0, 10);
            Assert.Equal(originals.Count, pending.Count);
            for (var i = 0; i < originals.Count; i++)
            {
                Assert.Equal((originals[i].Seq, originals[i].DeliveryId, originals[i].CreatedAt, originals[i].TargetAddress),
                    (pending[i].Seq, pending[i].DeliveryId, pending[i].CreatedAt, pending[i].TargetAddress));
                Assert.Equal(originals[i].Payload.ToArray(), pending[i].Payload.ToArray());
                Assert.Equal(row.TargetFingerprint, pending[i].Fingerprint);
            }
            Assert.True(factory.Services.GetRequiredService<OutputControlStore>().Read(1).ManuallyPaused);
            using var replay = await Confirm(admin, attempt);
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
            var finished = await admin.GetJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
            Assert.False(finished.GetProperty("canPrepareRetry").GetBoolean());
            Assert.Equal("configuration.migrationAlreadyCompleted", finished.GetProperty("retryProblem").GetString());
        }
        await using var restarted = Running(data.Path, out _);
        using var next = await restarted.CreateLoggedInClientAsync("operator", Token);
        var page = await next.GetJsonAsync(Root + "/intents?status=completed&connectionId=1", Token);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(intent.Hold.IntentId, item.GetProperty("id").GetGuid());
        Assert.Equal(attempt, item.GetProperty("lastOperationId").GetGuid());
        var outcome = await next.GetJsonAsync($"/api/edge-admin/v1/deliveries/migrations/{attempt}", Token);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal(2, restarted.Services.GetRequiredService<DeliveryCache>().GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("viewer")]
    [InlineData("configurator")]
    public async Task NewInspectionAndRetryEndpointsRemainAdministratorOnly(string role)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out _);
        using var client = role == "anonymous" ? factory.CreateClient() : await factory.CreateLoggedInClientAsync(role, Token);
        var expected = role == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        using var page = await client.GetAsync(Root + "/intents", Token);
        using var inspect = await client.GetAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
        using var prepare = await client.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
        using var current = await client.GetAsync(Root + "/intent-retry/preparation", Token);
        using var cancel = await client.DeleteAsync($"{Root}/intent-retry/preparation/{Guid.NewGuid()}", Token);
        using var confirm = await Confirm(client, Guid.NewGuid());
        foreach (var response in new[] { page, inspect, prepare, current, cancel, confirm })
        { Assert.Equal(expected, response.StatusCode); }
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
    }

    [Fact]
    public async Task SessionOwnershipCsrfBothDeclarationsCancellationAndMonotonicExpiryProtectPreparation()
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Running(data.Path, out var clock, services =>
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ConfigurationMigrationIntentService>(provider, time)));
        using var first = await factory.CreateLoggedInClientAsync("operator", Token);
        using var other = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var id = await Prepare(first, intent.Hold.IntentId);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await other.GetJsonAsync(Root + "/intent-retry/preparation", Token)).GetProperty("preparation").ValueKind);
        using var foreign = await Confirm(other, id);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var busy = await other.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        foreach (var request in new[] { new ConfigurationMigrationIntentConfirmation(false, true), new ConfigurationMigrationIntentConfirmation(true, false) })
        {
            using var invalid = await first.PostJsonAsync($"{Root}/intent-retry/{id}/confirm", request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var spoofed = await first.PostJsonAsync($"{Root}/intent-retry/{id}/confirm", new { confirmed = true, sameOwnerConfirmed = true, targetFingerprint = "forged" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        var csrf = first.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        first.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await Confirm(first, id);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        first.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        time.StepWallClock(TimeSpan.FromDays(5));
        Assert.Equal(id, (await first.GetJsonAsync(Root + "/intent-retry/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        using var cancelled = await first.DeleteAsync($"{Root}/intent-retry/preparation/{id}", Token);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var cancelledConfirm = await Confirm(first, id);
        Assert.Equal(HttpStatusCode.NotFound, cancelledConfirm.StatusCode);
        var expiring = await Prepare(first, intent.Hold.IntentId);
        time.Advance(ConfigurationMigrationIntentService.Lifetime);
        using var expired = await Confirm(first, expiring);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaAtPreparationOrExecutionRejectsEntireScopeAndAllowsOnlyANewReview(bool fillsLater)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock, services => services.AddSingleton(provider => new DeliveryCache(new()
        {
            DatabasePath = Path.Combine(data.Path, DeliveryCacheOptions.FileName),
            ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            MaximumPendingPerTarget = fillsLater ? 2 : 1,
        }, TimeProvider.System)));
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        using var preparation = await admin.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
        Assert.Equal(HttpStatusCode.OK, preparation.StatusCode);
        var review = await preparation.ReadJsonAsync(Token);
        Assert.Equal(fillsLater, review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(fillsLater ? 0 : 1, review.GetProperty("inspection").GetProperty("currentQuota").GetProperty("pendingRecords").GetInt32());
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        long added = 0;
        if (fillsLater)
        {
            var target = factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime.CaptureMigrationTarget(1, 3)!;
            added = Assert.Single((await cache.EnqueueAsync([new(Guid.NewGuid(), 1, Guid.NewGuid(), "later", 3,
                "{}"u8.ToArray(), target.Fingerprint, target.Summary, "send")], Token)).Seqs);
        }
        using var confirmed = await Confirm(admin, review.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, confirmed.StatusCode);
        if (fillsLater)
        { Assert.Equal(1, (await confirmed.ReadJsonAsync(Token)).GetProperty("migrationQuota").GetProperty("pendingRecords").GetInt32()); }
        Assert.Equal(2, cache.ReadQuarantined(1, 0, 10).Count);
        var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
        var last = store.ReadIntent(intent.Hold.IntentId)!.LastOperationId;
        Assert.Equal("rejected", store.ReadIntent(intent.Hold.IntentId)!.Status);
        await clock.PollAsync();
        Assert.Equal(last, store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
        var inspect = await admin.GetJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
        Assert.Equal(1, inspect.GetProperty("currentQuota").GetProperty("pendingRecords").GetInt32());
        if (fillsLater)
        {
            await cache.DeleteAsync([added], Token); // Removes a test-only later capacity occupant, not the held scope.
            var retry = await Prepare(admin, intent.Hold.IntentId);
            using var successful = await Confirm(admin, retry);
            Assert.Equal(HttpStatusCode.OK, successful.StatusCode);
            Assert.Equal("rejected", store.Read(Guid.Parse(last!))!.Status);
            Assert.Equal("completed", store.ReadIntent(intent.Hold.IntentId)!.Status);
        }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    public async Task DriftAfterReviewConsumesConfirmationWithoutChangingApproval(string change)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var id = await Prepare(admin, intent.Hold.IntentId);
        var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
        var original = store.ReadIntent(intent.Hold.IntentId)!;
        if (change == "identity")
        {
            await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(1,
                new(CurrentAddress, HttpCredentialKind.Basic, Username: "changed-after-review", Secret: "test-only"), "admin", null, true, token: TestContext.Current.CancellationToken);
        }
        else
        {
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.FreezeDraft(MigrationIntentFixture.Plan("http://later.test/"), "0", "admin"), MigrationIntentFixture.Application);
        }
        using var stale = await Confirm(admin, id);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var consumed = await Confirm(admin, id);
        Assert.Equal(HttpStatusCode.NotFound, consumed.StatusCode);
        Assert.True(DeliveryMigrationStore.MatchesRetryReview(store.ReadIntent(intent.Hold.IntentId)!, original));
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
    }

    [Theory]
    [InlineData("delivery.migrationIntentRetried")]
    [InlineData("delivery.migrationStarted")]
    public async Task RetryAuditFailureRollsBackNewApprovalAttemptAndPointerTogether(string action)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var id = await Prepare(admin, intent.Hold.IntentId);
        var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
        var original = store.ReadIntent(intent.Hold.IntentId)!;
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_retry BEFORE INSERT ON audit_log WHEN NEW.action='{action}' BEGIN SELECT abs(-9223372036854775808); END;");
        using var failed = await Confirm(admin, id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(DeliveryMigrationStore.MatchesRetryReview(store.ReadIntent(intent.Hold.IntentId)!, original));
        Assert.Null(store.Read(id));
        Assert.Null(factory.Services.GetRequiredService<DeliveryCache>().ReadMigrationReceipt(id));
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
        database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_retry");
        var next = await Prepare(admin, intent.Hold.IntentId);
        using var success = await Confirm(admin, next);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task MetadataPaginationFiltersAndSqlProjectionDoNotFetchIdentitySummaries()
    {
        using var data = TemporaryDirectory.Create();
        _ = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var template = database.Client.Queryable<DeliveryMigrationIntentRecord>().First();
        for (var index = 2; index <= 105; index++)
        {
            var row = database.Client.Queryable<DeliveryMigrationIntentRecord>().InSingle(template.IntentId);
            row.IntentId = Guid.NewGuid().ToString("D");
            row.ConnectionId = index;
            row.Status = index % 2 == 0 ? "waiting" : "rejected";
            row.LastOperationId = null;
            row.RegisteredAt = 1000 - index;
            database.Client.Insertable(row).ExecuteCommand();
        }
        var foreign = database.Client.Queryable<DeliveryMigrationIntentRecord>().InSingle(template.IntentId);
        foreign.IntentId = Guid.NewGuid().ToString("D");
        foreign.ConnectionId = 999;
        foreign.ConfigStoreId = "different-store";
        database.Client.Insertable(foreign).ExecuteCommand();
        ConcurrentQueue<string> commands = new();
        // SqlSugarScope copies the root config into each request/CopyNew context; observing this test's
        // CurrentConnectionConfig would silently miss the API's real SQL. Pin the same root seam as the process gates.
        var scope = Assert.IsType<SqlSugarScope>(database.Client);
        var root = typeof(SqlSugarScope).GetField("_configs", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(root);
        var events = Assert.Single(Assert.IsType<List<ConnectionConfig>>(root.GetValue(scope))).AopEvents;
        var previous = events.OnLogExecuted;
        events.OnLogExecuted = (sql, _) => commands.Enqueue(sql);
        try
        {
            var first = await admin.GetJsonAsync(Root + "/intents?limit=100", Token);
            Assert.Equal(100, first.GetProperty("items").GetArrayLength());
            Assert.Equal(100, first.GetProperty("next").GetProperty("connectionId").GetInt64());
            Assert.DoesNotContain("sourceSummary", first.GetRawText(), StringComparison.Ordinal);
            var rest = await admin.GetJsonAsync(Root + "/intents?limit=100&afterRevision=2&afterConnection=100", Token);
            Assert.Equal(5, rest.GetProperty("items").GetArrayLength());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, rest.GetProperty("next").ValueKind);
            var selected = await admin.GetJsonAsync(Root + "/intents?status=rejected&connectionId=1", Token);
            Assert.Single(selected.GetProperty("items").EnumerateArray());
            var empty = await admin.GetJsonAsync(Root + "/intents?status=completed", Token);
            Assert.Empty(empty.GetProperty("items").EnumerateArray());
            var queries = commands.Where(sql => sql.Contains("delivery_migration_intent", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.NotEmpty(queries);
            Assert.All(queries, sql =>
            {
                Assert.DoesNotContain("source_summary", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("old_fingerprint", sql, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally { events.OnLogExecuted = previous; }
        foreach (var query in new[] { "status=started", "limit=0", "limit=101", "connectionId=0", "connectionId=9007199254740992", "afterRevision=2", "afterConnection=1", "afterRevision=0&afterConnection=1" })
        {
            using var invalid = await admin.GetAsync(Root + "/intents?" + query, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsNeverReconcileOrReplayUnknownStartedAttemptsEvenWhenQueueHasCommitted(bool committed)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
        var runtime = factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
        var expected = store.ReadIntent(intent.Hold.IntentId)!;
        var target = runtime.CaptureMigrationTarget(1, 3)!;
        var command = DeliveryMigrationCommand.ForIntent(intent.Hold, Guid.NewGuid(), target.Fingerprint, target.Summary);
        // Hold the real persistent start/result boundary using the production stores, without calling Finish.
        store.BeginIntentRetry(command, 3, expected, "operator", null);
        if (committed)
        { Assert.True((await runtime.Deliveries.MigrateAsync(command)).Committed); }
        var finishedCount = TestAccounts.Audit(factory.Services, "delivery.migrationFinished").Count;
        var inspection = await admin.GetJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
        Assert.False(inspection.GetProperty("canPrepareRetry").GetBoolean());
        using var denied = await admin.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await clock.PollAsync();
        Assert.Equal("started", store.Read(command.OperationId)!.Status);
        Assert.Equal(command.OperationId.ToString("D"), store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
        Assert.Equal(finishedCount, TestAccounts.Audit(factory.Services, "delivery.migrationFinished").Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "MES retry")]
    [InlineData("MES apply", null)]
    [InlineData("MES apply", "MES retry")]
    public async Task StaleApprovalCannotReplaceStartedAttemptAndInterruptedContinuationKeepsRetryActor(string? originalToken, string? retryToken)
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path, tokenName: originalToken);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
        var runtime = factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
        var expected = store.ReadIntent(intent.Hold.IntentId)!;
        var target = runtime.CaptureMigrationTarget(1, 3)!;
        DeliveryMigrationCommand Command() => DeliveryMigrationCommand.ForIntent(intent.Hold, Guid.NewGuid(), target.Fingerprint, target.Summary);
        var first = Command();
        store.BeginIntentRetry(first, 3, expected, new AuditAccount("operator", retryToken), "192.0.2.22");
        Assert.Throws<InvalidOperationException>(() => store.BeginIntentRetry(Command(), 3, expected, "admin", null));
        store.RecoverInterrupted(); // Store-only recovery boundary; no execution has ever owned this fixture attempt.
        Assert.Equal("interrupted", store.Read(first.OperationId)!.Status);
        var resumed = Command();
        store.BeginIntent(resumed, 3);
        Assert.Equal(("operator", "192.0.2.22"), (store.Read(resumed.OperationId)!.AccountName, store.Read(resumed.OperationId)!.SourceIp));
        Assert.Throws<InvalidOperationException>(() => store.BeginIntentRetry(Command(), 3, expected, "admin", null));
        store.Finish(resumed.OperationId, await runtime.Deliveries.MigrateAsync(resumed));
        Assert.Equal("completed", store.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Equal(retryToken, Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried")).TokenName);
        Assert.Equal(originalToken, Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRegistered")).TokenName);
        Assert.Equal(originalToken, store.ReadIntent(intent.Hold.IntentId)!.TokenName);
        foreach (var operation in new[] { first.OperationId, resumed.OperationId })
        {
            Assert.Equal(retryToken, store.Read(operation)!.TokenName);
            foreach (var action in new[] { "delivery.migrationStarted", "delivery.migrationFinished" })
            {
                var audit = Assert.Single(TestAccounts.Audit(factory.Services, action), row => row.Target == operation.ToString("D"));
                Assert.Equal("operator", audit.AccountName);
                Assert.Equal(retryToken, audit.TokenName);
                Assert.Equal("192.0.2.22", audit.SourceIp);
            }
        }
    }

    [Fact]
    public async Task ResultAuditFailureRemainsUnknownUntilNormalStartupReconcilesQueueReceipt()
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        Guid attempt;
        await using (var factory = Running(data.Path, out var clock))
        {
            using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
            await clock.PollAsync();
            attempt = await Prepare(admin, intent.Hold.IntentId);
            var database = factory.Services.GetRequiredService<ConfigDatabase>();
            database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_finish BEFORE INSERT ON audit_log WHEN NEW.action='delivery.migrationFinished' BEGIN SELECT abs(-9223372036854775808); END;");
            using var unknown = await Confirm(admin, attempt);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
            var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
            Assert.Equal("started", store.Read(attempt)!.Status);
            Assert.Equal(2, factory.Services.GetRequiredService<DeliveryCache>().ReadMigrationReceipt(attempt)!.AffectedRecords);
            using var denied = await admin.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            await clock.PollAsync();
            Assert.Equal(attempt.ToString("D"), store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationFinished")); // Only original rejection.
            database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_finish");
        }
        await using var restarted = Running(data.Path, out _);
        using var next = await restarted.CreateLoggedInClientAsync("operator", Token);
        var outcome = await next.GetJsonAsync($"/api/edge-admin/v1/deliveries/migrations/{attempt}", Token);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal(2, outcome.GetProperty("affectedRecords").GetInt32());
        Assert.Equal(2, TestAccounts.Audit(restarted.Services, "delivery.migrationFinished").Count);
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationIntentRetried"));
    }

    [Fact]
    public async Task InspectionPreparationAndConfirmationShareTheExistingSessionBudget()
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var id = await Prepare(admin, intent.Hold.IntentId);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(admin.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using (budget.TryAcquire(key)!)
        using (budget.TryAcquire(key)!)
        {
            using var inspect = await admin.GetAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
            using var prepare = await admin.PostJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/retry/prepare", new { }, Token);
            using var confirm = await Confirm(admin, id);
            foreach (var result in new[] { inspect, prepare, confirm })
            { Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode); }
        }
        Assert.Equal(id, (await admin.GetJsonAsync(Root + "/intent-retry/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task EmptyHeldScopeCanBeClosedWithoutCapturingLaterRecordsWithTheSameOldFingerprint()
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var held = cache.ReadQuarantined(1, 0, 10);
        await cache.DeleteAsync(held.Select(row => row.Seq).ToArray(), Token); // Fixture models removal of the original held records.
        var laterId = Guid.NewGuid();
        Assert.True((await cache.EnqueueAsync([new(laterId, 1, Guid.NewGuid(), "later", 3,
            "{}"u8.ToArray(), intent.Hold.OldFingerprint, intent.Source.Freeze(), "send")], Token)).Committed);
        var inspection = await admin.GetJsonAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
        Assert.Equal(0, inspection.GetProperty("remainingRecords").GetInt32());
        var retry = await Prepare(admin, intent.Hold.IntentId);
        using var result = await Confirm(admin, retry);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(0, (await result.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
        var unchanged = Assert.Single(cache.ReadPending(1, 0, 10).Concat(cache.ReadQuarantined(1, 0, 10)));
        Assert.Equal((laterId, intent.Hold.OldFingerprint), (unchanged.DeliveryId, unchanged.Fingerprint));
        Assert.Null(unchanged.MigrationId);
        Assert.Equal(0, cache.ReadMigrationReceipt(retry)!.AffectedRecords);
    }

    [Fact]
    public async Task MissingIntentAndUnreadableMetadataAreNotReportedAsEmptyOrZero()
    {
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path);
        await using var factory = Running(data.Path, out var clock);
        using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
        await clock.PollAsync();
        using var missing = await admin.GetAsync($"{Root}/intents/{Guid.NewGuid()}/preflight", Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("ALTER TABLE delivery_migration_intent RENAME TO unavailable_intents");
        try
        {
            using var page = await admin.GetAsync(Root + "/intents", Token);
            using var inspection = await admin.GetAsync($"{Root}/intents/{intent.Hold.IntentId}/preflight", Token);
            await ProblemAssert.HasStatusAsync(page, HttpStatusCode.ServiceUnavailable, Token);
            await ProblemAssert.HasStatusAsync(inspection, HttpStatusCode.ServiceUnavailable, Token);
        }
        finally { database.Client.Ado.ExecuteCommand("ALTER TABLE unavailable_intents RENAME TO delivery_migration_intent"); }
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledHttpWaitDoesNotReleaseRetryOwnershipAndRealSenderPreservesFrozenFailedRecord(bool stop)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        ConcurrentQueue<(string Body, string Key)> received = new();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MapPost("/current/send", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(Token);
            received.Enqueue((body, context.Request.Headers["Idempotency-Key"].ToString()));
            if (body == "{\"probe\":true}")
            { sending.TrySetResult(); await release.Task; }
            context.Response.StatusCode = 204;
        });
        await peer.StartAsync(Token);
        var address = new Uri(peer.Urls.Single());
        using var data = TemporaryDirectory.Create();
        var intent = await Seed(data.Path, currentAddress: new Uri(address, "/current/").AbsoluteUri, failedRecord: true);
        await using var factory = Running(data.Path, out var clock, settings: new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) });
        try
        {
            using var admin = await factory.CreateLoggedInClientAsync("operator", Token);
            await clock.PollAsync();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var cache = coordinator.Runtime.Deliveries;
            var original = cache.ReadQuarantined(1, 0, 10);
            var target = coordinator.Runtime.CaptureMigrationTarget(1, 3)!;
            var probeId = Guid.NewGuid();
            Assert.True((await cache.EnqueueAsync([new(probeId, 1, Guid.NewGuid(), "later", 3, "{\"probe\":true}"u8.ToArray(),
                target.Fingerprint, target.Summary, "send", headers: new Dictionary<string, string> { ["Idempotency-Key"] = probeId.ToString("D") },
                protocolMetadata: HttpFrozenRequest.Freeze(new HttpOutputSettings { Path = "send" }))], Token)).Committed);
            var controls = factory.Services.GetRequiredService<OutputControlStore>();
            controls.Write(1, new(false, true, controls.Read(1).ResumeVersion), "operator", null);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var attempt = await Prepare(admin, intent.Hold.IntentId);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var request = admin.PostJsonAsync($"{Root}/intent-retry/{attempt}/confirm", new ConfigurationMigrationIntentConfirmation(true, true), caller.Token);
            var store = factory.Services.GetRequiredService<DeliveryMigrationStore>();
            await OutputPipelineTests.UntilAsync(() => store.Read(attempt)?.Status == "started" && coordinator.Status.QueueOperationInProgress);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await request; });
            Assert.Equal(2, cache.ReadQuarantined(1, 0, 10).Count);
            Assert.Null(cache.ReadMigrationReceipt(attempt));
            await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync("0", "operator", null, Token));
            var stopping = stop ? coordinator.StopAsync(Token) : null;
            if (stopping is not null)
            { Assert.False(stopping.IsCompleted); }
            release.TrySetResult();
            await OutputPipelineTests.UntilAsync(() => store.Read(attempt)?.Status == "completed");
            Assert.Equal(2, cache.ReadMigrationReceipt(attempt)!.AffectedRecords);
            var failed = Assert.Single(cache.ReadFailed(1, 0, 10));
            Assert.Equal(original[0].DeliveryId, failed.DeliveryId);
            Assert.Equal(original[0].Payload.ToArray(), failed.Payload.ToArray());
            Assert.Equal(0, failed.FailureCount);
            Assert.Equal("operator", store.Read(attempt)!.AccountName);
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationIntentRetried"));
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.Equal(RuntimeCoordinatorState.Stopped, coordinator.Status.State);
            }
            else
            {
                await OutputPipelineTests.UntilAsync(() => received.Count == 2 && cache.ReadPending(1, 0, 10).Count == 0);
                Assert.Equal((Encoding.UTF8.GetString(original[1].Payload.Span), original[1].DeliveryId.ToString("D")), received.ToArray()[1]);
                using var query = await admin.GetAsync($"/api/edge-admin/v1/deliveries/migrations/{attempt}", Token);
                Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            }
        }
        finally { release.TrySetResult(); await peer.StopAsync(CancellationToken.None); }
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Guid id) =>
        client.PostJsonAsync($"{Root}/intent-retry/{id}/confirm", new ConfigurationMigrationIntentConfirmation(true, true), Token);
    private static async Task<Guid> Prepare(HttpClient client, Guid intent)
    {
        using var response = await client.PostJsonAsync($"{Root}/intents/{intent}/retry/prepare", new { }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var review = await response.ReadJsonAsync(Token);
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        return review.GetProperty("id").GetGuid();
    }
    private static FixedDataDirectoryApplicationFactory Running(string path, out MigrationPollClock clock, Action<IServiceCollection>? configure = null,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        var captured = clock = new MigrationPollClock();
        return new(path, settings, configureServices: services => { services.AddSingleton(provider => ActivatorUtilities.CreateInstance<RuntimeCoordinator>(provider, captured)); configure?.Invoke(services); });
    }
    private static async Task<DeliveryMigrationIntentDraft> Seed(string path, bool legacy = false, string currentAddress = CurrentAddress, bool failedRecord = false, bool mqtt = false, string? tokenName = null)
    {
        await using var setup = new FixedDataDirectoryApplicationFactory(path);
        var intent = mqtt ? await RegisterMqtt(setup.Services) : await MigrationIntentFixture.RegisterAsync(setup.Services, tokenName: tokenName);
        var runtime = setup.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
        if (failedRecord)
        { await runtime.Deliveries.RecordFailureAsync(new(runtime.Deliveries.ReadPending(1, 0, 10)[0].Seq, 4, "original failed record", true, false), Token); }
        Assert.True((await runtime.HoldMigrationAsync(intent.Hold, null, Token)).Committed);
        var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
        var plan = mqtt ? MqttPlan("current.test") : RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new[]
        { new { kind = "output.http", key = "queue", configuration = new { connectionId = 1, baseAddress = currentAddress,
            path = "", timeoutMs = 60000, queueOnly = true, inputs = Array.Empty<string>() } } }
        }));
        versions.Commit(versions.FreezeDraft(plan, "0", "admin"), MigrationIntentFixture.Application);
        if (legacy)
        { setup.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE delivery_migration_intent SET target_fingerprint=NULL, target_summary=NULL WHERE intent_id=@id", new { id = intent.Hold.IntentId.ToString("D") }); }
        setup.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "admin", null);
        foreach (var (account, role) in new[] { ("admin", UserRole.Administrator), ("operator", UserRole.Administrator), ("viewer", UserRole.Viewer), ("configurator", UserRole.Configurator) })
        { TestAccounts.Create(setup.Services, account, ApiRequests.StrongPassword, role); }
        return intent;
    }
    private static RuntimePlanDocument MqttPlan(string host) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        units = new[] { new { kind = "output.mqtt", key = "queue",
            configuration = new { connectionId = 1, host, queueOnly = true, inputs = Array.Empty<string>() } } },
    }));
    private static async Task<DeliveryMigrationIntentDraft> RegisterMqtt(IServiceProvider services)
    {
        static OutputTargetIdentity Identity(string host)
        {
            var settings = new MqttOutputSettings { ConnectionId = 1, Host = host, QueueOnly = true };
            return new(settings.Fingerprint(null, null), OutputRecipientIdentity.Mqtt(settings.Recipient, false, null));
        }
        var source = Identity("old.test");
        var target = Identity("new.test");
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(MqttPlan("old.test"), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var intent = new DeliveryMigrationIntentDraft(new(services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), 1, source.Fingerprint),
            source.Recipient, target.Recipient.Address, true, target);
        var records = Enumerable.Range(0, 2).Select(_ => new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            "{}"u8.ToArray(), source.Fingerprint, source.Summary, "frozen/topic", 1)).ToArray();
        Assert.Equal(2, (await services.GetRequiredService<DeliveryCache>().EnqueueAsync(records, Token)).Seqs.Count);
        versions.Commit(versions.FreezeDraft(MqttPlan("new.test"), "0", "admin"), MigrationIntentFixture.Application, markRunning: true, migrationIntents: [intent]);
        versions.ClearRunningMarker(2); // This fixture registered the plan but never activated it; not crash evidence.
        return intent;
    }
}
