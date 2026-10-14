using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class RuntimeParameterTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/runtime-parameters";
    private const string Action = "system.runtimeParametersChanged";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly int[] ConcurrentDays = [365, 730];

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task AllRolesReadButOnlyAdministratorChanges(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var get = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl!.NoStore);
        var current = await Read(client);
        Assert.Equal(new RuntimeParameterValues(180, 4, 2), current.Values);
        using var put = await Write(client, new(365, 6, 3), current.Version);
        Assert.Equal(expected, put.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, Action).Count);
    }

    [Fact]
    public async Task AnonymousCsrfAndConfirmationAreRequired()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var put = await Write(anonymous, new(365, 6, 3), null);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var unconfirmed = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new RuntimeParameterRequest(new(365, 6, 3), null, false), Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await Write(client, new(365, 6, 3), null);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Theory]
    [InlineData(179, 4, 2)]
    [InlineData(36501, 4, 2)]
    [InlineData(180, 0, 1)]
    [InlineData(180, 65, 2)]
    [InlineData(180, 4, 0)]
    [InlineData(180, 4, 5)]
    public async Task OutOfRangeValuesDoNotPublishOrPersist(int days, int global, int session)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var invalid = await Write(client, new(days, global, session), null);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null((await Read(client)).Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task RequiredAndUnknownFieldsAreRejectedAndBoundaryValuesAreAccepted()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var body in new[] { "{}", "{\"values\":null,\"expectedVersion\":null,\"confirmed\":true}",
            "{\"values\":{\"auditRetentionDays\":365,\"previewGlobalLimit\":4,\"previewSessionLimit\":2},\"confirmed\":true}",
            "{\"values\":{\"auditRetentionDays\":365,\"previewGlobalLimit\":4,\"previewSessionLimit\":2,\"unknown\":1},\"expectedVersion\":null,\"confirmed\":true}",
            "{\"values\":{\"auditRetentionDays\":365,\"previewGlobalLimit\":4,\"previewSessionLimit\":2},\"expectedVersion\":null,\"confirmed\":true,\"unknown\":1}" })
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(Endpoint, content, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var high = await Write(client, new(36500, 64, 64), null);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
        using var low = await Write(client, new(180, 1, 1), (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, low.StatusCode);
    }

    [Fact]
    public async Task ChangesSurviveRestartDeploymentAuditFloorIsPreservedAndNoOpDoesNotDuplicateAudit()
    {
        using var data = TemporaryDirectory.Create();
        string? version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [DataRetentionSettings.AuditRetentionDaysKey] = "365" }))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var client = await factory.CreateLoggedInClientAsync("admin", Token);
            Assert.Equal(365, (await Read(client)).DeploymentAuditMinimumDays);
            using var shortValue = await Write(client, new(180, 4, 2), null);
            Assert.Equal(HttpStatusCode.BadRequest, shortValue.StatusCode);
            using var write = await Write(client, new(730, 8, 3, 67584, 135168, 2, 32 * 1024 * 1024, 7), null);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            version = (await Read(client)).Version;
            using var noOp = await Write(client, new(730, 8, 3, 67584, 135168, 2, 32 * 1024 * 1024, 7), version);
            Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
            Assert.Equal(version, (await Read(client)).Version);
            Assert.Single(TestAccounts.Audit(factory.Services, Action));
            var system = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(system.EffectiveRevision);
            Assert.Null(system.PendingRevision);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [DataRetentionSettings.AuditRetentionDaysKey] = "1000" });
        using var again = await restarted.CreateLoggedInClientAsync("admin", Token);
        var restored = await Read(again);
        Assert.Equal(version, restored.Version);
        Assert.Equal(new RuntimeParameterValues(1000, 8, 3, 67584, 135168, 2, 32 * 1024 * 1024, 7), restored.Values);
        Assert.Equal(32 * 1024 * 1024, restarted.Services.GetRequiredService<InFlightBudget>().Capacity);
        Assert.Equal(7, restarted.Services.GetRequiredService<InputChannelLimit>().Capacity);
        Assert.Single(TestAccounts.Audit(restarted.Services, Action));
    }

    [Fact]
    public async Task ConcurrentWritesHaveOneWinnerAndAuditFailureDoesNotPublish()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var results = await Task.WhenAll(ConcurrentDays.Select(days => Write(client, new(days, 6, 3), null)));
        try
        {
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in results) { response.Dispose(); } }
        var before = await Read(client);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER reject_parameters BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture audit failure'); END;");
        using var failed = await Write(client, new(1095, 8, 4), before.Version);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, await Read(client));
        Assert.Equal(before.Version, database.Client.Queryable<RuntimeParameterRecord>().InSingle(1).Version);
        Assert.Single(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task SameBudgetInstanceUsesNewLimitsWithoutReleasingExistingLeases()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire("one");
        using var second = budget.TryAcquire("two");
        Assert.NotNull(first);
        Assert.NotNull(second);
        using var low = await Write(client, new(180, 1, 1), null);
        Assert.Equal(HttpStatusCode.OK, low.StatusCode);
        Assert.Same(budget, factory.Services.GetRequiredService<PreviewOperationBudget>());
        using var denied = await client.PostAsJsonAsync(new Uri("/api/edge-admin/v1/connections/inspect-outbound", UriKind.Relative), new { host = "203.0.113.1", port = 443 }, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        first.Dispose();
        Assert.Null(budget.TryAcquire("three"));
        second.Dispose();
        using var allowed = budget.TryAcquire("three");
        Assert.NotNull(allowed);
        using var raised = await Write(client, new(180, 3, 2), (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        using var next = budget.TryAcquire("three");
        Assert.NotNull(next);
        Assert.Null(budget.TryAcquire("three"));
    }

    [Fact]
    public async Task RetentionAndExportGateExcludeWritesAndPublishedRetentionIsUsedByRealCleanup()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var gate = factory.Services.GetRequiredService<AuditRetentionGate>();
        using (var snapshot = gate.TryEnterSnapshot())
        {
            Assert.NotNull(snapshot);
            using var busy = await Write(client, new(365, 4, 2), null);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        }
        using (var purge = gate.TryEnterPurge())
        {
            Assert.NotNull(purge);
            using var busy = await Write(client, new(365, 4, 2), null);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        }
        var now = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.InTransaction(c => c.Insertable(new AuditLogRecord
        {
            Id = 1,
            OccurredAt = now.AddDays(-200).ToUnixTimeMilliseconds(),
            Action = "fixture.old",
            Result = "success"
        }).ExecuteCommand());
        using var cleanup = new DataRetentionService(database, new(TimeSpan.FromDays(180)), new ManualTimeProvider(now),
            new TestClockSyncStatus(), NullLogger<DataRetentionService>.Instance, gate, () =>
            {
                var parameters = factory.Services.GetRequiredService<RuntimeParameterStore>();
                var current = parameters.Read();
                // Regression: read the cutoff only while the purge gate excludes writes.
                Assert.Throws<RuntimeParameterBusyException>(() => parameters.Write(new(new(400, 4, 2), current.Version, true), Actor(factory.Services)));
                return factory.Services.GetRequiredService<Func<TimeSpan>>()();
            });
        using var longer = await Write(client, new(365, 4, 2), null);
        Assert.Equal(HttpStatusCode.OK, longer.StatusCode);
        Assert.Equal(0, cleanup.PurgeExpired().AuditRecords);
        Assert.True(database.Client.Queryable<AuditLogRecord>().Any(row => row.Id == 1));
        using var shorter = await Write(client, new(180, 4, 2), (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, shorter.StatusCode);
        Assert.Equal(1, cleanup.PurgeExpired().AuditRecords);
        Assert.False(database.Client.Queryable<AuditLogRecord>().Any(row => row.Id == 1));
    }

    [Fact]
    public void MigrationFrom22PreservesIdentityAndDoesNotInventStoredOverrides()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(m => m.Version <= 22).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        Assert.Equal(ConfigMigrations.All.Max(m => m.Version), store.CreateMigrator().Migrate());
        Assert.Equal(ConfigMigrations.All.Max(m => m.Version), store.CreateMigrator().Migrate());
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out _));
        Assert.Empty(store.Database.Client.Queryable<RuntimeParameterRecord>().ToList());
    }

    [Fact]
    public async Task CorruptStoredVersionIsRejectedByStartupLoadRatherThanFallingBack()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var parameters = factory.Services.GetRequiredService<RuntimeParameterStore>();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        parameters.Write(new(new(365, 4, 2), null, true), Actor(factory.Services));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("UPDATE runtime_parameters SET version='invalid' WHERE id=1");
        var reload = new RuntimeParameterStore(database, factory.Services.GetRequiredService<AuditTrail>(),
            factory.Services.GetRequiredService<DataRetentionSettings>(), TimeProvider.System,
            factory.Services.GetRequiredService<AuditRetentionGate>(), new RuntimeMessageCache(TimeProvider.System), new InFlightBudget(67108864, 8388608), new InputChannelLimit(100));
        var error = Assert.Throws<HostStartupException>(() => reload.Read());
        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, error.ExitCode);
    }

    [Fact]
    public async Task DebugLimitsApplyToOriginalCacheAndAuditFailureCannotEvictOrPublish()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var cache = factory.Services.GetRequiredService<RuntimeMessageCache>();
        Assert.Same(cache, factory.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>().Messages);
        var id = TenonEdge.Abstractions.Plans.RuntimeUnitId.Create("input.http", "device/source");
        for (var i = 0; i < 10; i++)
        { cache.Capture(id, 1, Guid.NewGuid(), "fixture"u8); }
        var before = cache.Read(id, true);
        var current = await Read(client);
        var low = current.Values with { DebugObjectRecords = 2, DebugObjectBytes = 67584, DebugGlobalBytes = 67584 };
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER reject_debug BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        using var failed = await Write(client, low, current.Version);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(before, cache.Read(id, true));
        Assert.Equal(current, await Read(client));
        database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_debug");
        using var saved = await Write(client, low, current.Version);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(before.Take(2), cache.Read(id, true));
        for (var i = 0; i < 10; i++)
        { cache.Capture(id, 1, Guid.NewGuid(), "new fixture"u8); }
        Assert.Equal(2, cache.Read(id, true).Count);
        Assert.Single(TestAccounts.Audit(factory.Services, Action));
    }

    [Theory]
    [InlineData(67583, 33554432, 100)]
    [InlineData(67108865, 33554432, 100)]
    [InlineData(1048576, 1048575, 100)]
    [InlineData(1048576, 1073741825, 100)]
    [InlineData(1048576, 33554432, 0)]
    [InlineData(1048576, 33554432, 101)]
    public async Task InvalidDebugLimitsAreRejected(int perObject, int global, int count)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var invalid = await Write(client, new(180, 4, 2, perObject, global, count), null);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null((await Read(client)).Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task ChangedAdministratorCannotCommitEvenAfterRequestAuthorization()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var actor = Actor(factory.Services);
        TestAccounts.Update(factory.Services, "admin", user => user.Role = "Viewer");
        var store = factory.Services.GetRequiredService<RuntimeParameterStore>();
        Assert.Throws<TenonEdge.Host.Features.Tokens.ApiTokenException>(() => store.Write(new(new(365, 4, 2), null, true), actor));
        Assert.Null(store.Read().Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public void MigrationFrom25PreservesExistingPolicyAndAddsDefaultDebugLimits()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(m => m.Version <= 25).ToArray());
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO runtime_parameters(id,version,audit_retention_days,preview_global_limit,preview_session_limit,updated_at) VALUES(1,'11111111111111111111111111111111',365,8,3,1)");
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        var row = store.Database.Client.Queryable<RuntimeParameterRecord>().InSingle(1);
        Assert.Equal(365, row.AuditRetentionDays);
        Assert.Equal(8, row.PreviewGlobalLimit);
        Assert.Equal(3, row.PreviewSessionLimit);
        Assert.Equal(1048576, row.DebugObjectBytes);
        Assert.Equal(33554432, row.DebugGlobalBytes);
        Assert.Equal(100, row.DebugObjectRecords);
    }

    [Fact]
    public async Task InFlightChangeUsesOriginalRuntimeBudgetAndRollbackKeepsOldAdmission()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var budget = factory.Services.GetRequiredService<InFlightBudget>();
        Assert.Same(budget, factory.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>().InFlight);
        Assert.True(budget.TryReserve(40 * 1024 * 1024));
        var current = await Read(client);
        var lower = current.Values with { InFlightBytes = 16 * 1024 * 1024 };
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER reject_inflight BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        using var failed = await Write(client, lower, current.Version);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(64 * 1024 * 1024, budget.Capacity);
        database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_inflight");
        using var changed = await Write(client, lower, current.Version);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(lower.InFlightBytes, budget.Capacity);
        Assert.Equal(40 * 1024 * 1024, budget.Status.ReservedBytes);
        Assert.False(budget.TryReserve(1));
        budget.Release(40 * 1024 * 1024);
        Assert.True(budget.TryReserve(8 * 1024 * 1024));
        Assert.False(budget.TryReserve(1));
        budget.Release(8 * 1024 * 1024);
        using var high = await Write(client, lower with { InFlightBytes = 1024 * 1024 * 1024 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
        Assert.Equal(1024 * 1024 * 1024, budget.Capacity);
        foreach (var invalid in new[] { 16777215L, 1073741825L })
        {
            using var response = await Write(client, lower with { InFlightBytes = invalid }, (await Read(client)).Version);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(1024 * 1024 * 1024, budget.Capacity);
    }

    [Fact]
    public async Task InputCountPolicyUpdatesExistingChannelOnlyAfterCommit()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var limit = factory.Services.GetRequiredService<InputChannelLimit>();
        Assert.Same(limit, factory.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>().InputChannels);
        var bytes = new InFlightBudget(100000);
        var channel = new BudgetedInputChannel(100, bytes, limits: limit);
        var now = DateTimeOffset.UtcNow;
        var message = new TenonEdge.Abstractions.Messages.EdgeMessage(Guid.NewGuid(), "input", "device", now,
            TenonEdge.Abstractions.Messages.TimeSource.Gateway, now, new Dictionary<string, TenonEdge.Abstractions.Messages.FieldValue>());
        Assert.True(channel.Writer.TryWrite(message));
        var current = await Read(client);
        var low = current.Values with { InputChannelCapacity = 1 };
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER reject_count BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        using var failed = await Write(client, low, current.Version);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(100, limit.Capacity);
        database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_count");
        using var saved = await Write(client, low, current.Version);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False(channel.Writer.TryWrite(message));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.True(channel.Writer.TryWrite(message));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, bytes.Status.ReservedBytes);
        foreach (var value in new[] { 0, 10001 })
        {
            using var invalid = await Write(client, low with { InputChannelCapacity = value }, (await Read(client)).Version);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var high = await Write(client, low with { InputChannelCapacity = 10000 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
        Assert.Equal(10000, limit.Capacity);
    }

    private static ManagementActor Actor(IServiceProvider services)
    {
        var user = TestAccounts.Find(services, "admin")!;
        return new(user.Id, user.SecurityStamp, null);
    }

    private static Task<HttpResponseMessage> Write(HttpClient client, RuntimeParameterValues values, string? version) =>
        client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new RuntimeParameterRequest(values, version, true), Token);
    private static async Task<RuntimeParameterState> Read(HttpClient client) =>
        (await client.GetFromJsonAsync<RuntimeParameterState>(Endpoint, Token))!;
}
