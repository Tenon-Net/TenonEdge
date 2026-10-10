using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationRuntimeRetryTests
{
    private const string Root = "/api/edge-admin/v1/configuration/runtime";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task OnlyEditorsCanRetryAndTheEffectiveRevisionAndDirtyDraftAreNotReplaced(string roleName)
    {
        using var data = TemporaryDirectory.Create();
        var role = Enum.Parse<UserRole>(roleName);
        await InitializeAsync(data.Path, role);
        var state = new RuntimeStateReader(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
        using var read = await client.GetAsync(Root, Token);
        Assert.True(read.Headers.CacheControl!.NoStore);
        var status = await read.ReadJsonAsync(Token);
        Assert.True(status.GetProperty("canRetry").GetBoolean());
        Assert.Equal(1, status.GetProperty("effectiveRevision").GetInt64());
        var before = state.Snapshot(1);
        var drafts = factory.Services.GetRequiredService<DeviceDraftStore>();
        var draftBefore = drafts.Read();
        using var response = await client.PostJsonAsync(Root + "/retry", Request(status), Token);
        if (role == UserRole.Viewer)
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Forbidden, Token);
            Assert.Equal(before, state.Snapshot(1));
            Assert.Empty(coordinator.ReadApplications());
            return;
        }
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var id = (await response.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
        var terminal = await client.GetJsonAsync(response.Headers.Location!.OriginalString, Token);
        Assert.Equal("retry", terminal.GetProperty("kind").GetString());
        Assert.Equal("completed", terminal.GetProperty("stage").GetString());
        Assert.False(terminal.GetProperty("committed").GetBoolean()); // No new configuration commit.
        Assert.True(terminal.GetProperty("result").GetProperty("activated").GetBoolean());
        Assert.Equal(1, coordinator.Runtime.ActiveRevision);
        Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetStatus().Units.Any(unit => unit.Reads?.ReadsCompleted > 0));
        Assert.Contains(coordinator.Runtime.GetStatus().Units, unit => unit.Id.Kind == "input.testretry" && unit.State == RuntimeUnitState.BuildFailed);
        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
        Assert.Equal(before with { AbnormalExitCount = 0 }, state.Snapshot(1));
        Assert.Equal(1, state.Pointers().Running);
        Assert.Single(state.Snapshots());
        Assert.Equal(draftBefore.Version, drafts.Read().Version);
        Assert.Equal(draftBefore.Devices, drafts.Read().Devices);
        var audit = Assert.Single(state.Audit(AuditActions.RuntimeRetried));
        Assert.Equal(("operator", "r1", "success"), (audit.Account, audit.Target, audit.Result));
        Assert.Single(state.Events("runtime.retried"));
        Assert.False((await client.GetJsonAsync(Root, Token)).GetProperty("canRetry").GetBoolean());
        using var replay = await client.PostJsonAsync(Root + "/retry", Request(status), Token);
        Assert.Contains("runtime.retryUnavailable", await ProblemAssert.HasStatusAsync(replay, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        Assert.Single(coordinator.ReadApplications());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("schema")]
    public async Task DamagedSnapshotStaysFailedAndReadableManagementDoesNotEnableOrPerformRetry(string damage)
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        var state = new RuntimeStateReader(data.Path);
        state.Execute(damage switch
        {
            "hash" => "UPDATE runtime_snapshot SET content_hash = 'private-damaged-detail' WHERE revision = 1",
            "schema" => "UPDATE runtime_snapshot SET schema_version = 99 WHERE revision = 1",
            _ => "DELETE FROM runtime_snapshot WHERE revision = 1",
        });
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var status = await client.GetJsonAsync(Root, Token);
        Assert.Equal("runFailed", status.GetProperty("state").GetString());
        Assert.Equal(1, status.GetProperty("effectiveRevision").GetInt64());
        Assert.False(status.GetProperty("canRetry").GetBoolean());
        Assert.Equal("configuration.invalidSnapshot", status.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("private-damaged-detail", status.GetRawText(), StringComparison.Ordinal);
        using var response = await client.PostJsonAsync(Root + "/retry", new ConfigurationRetryRequest(1, "sha256:" + new string('0', 64), true), Token);
        var problem = await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token);
        Assert.Contains("configuration.invalidSnapshot", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("private-damaged-detail", problem, StringComparison.Ordinal);
        Assert.Equal(1, state.Pointers().Effective);
        Assert.Null(state.Pointers().Running);
        Assert.Single(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
        Assert.Empty(state.Audit(AuditActions.RuntimeRetried));
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task AuthenticationCsrfConfirmationAndStaleCheckpointAreCheckedBeforeAdmission()
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var request = new ConfigurationRetryRequest(1, RuntimeSnapshotDocument.ReadPlan(versions.Load(1)!).ContentHash, true);
        using var anonymous = factory.CreateClient();
        using var deniedRead = await anonymous.GetAsync(Root, Token);
        await ProblemAssert.HasStatusAsync(deniedRead, HttpStatusCode.Unauthorized, Token);
        using var denied = await anonymous.PostJsonAsync(Root + "/retry", request, Token);
        await ProblemAssert.HasStatusAsync(denied, HttpStatusCode.Unauthorized, Token);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await client.PostJsonAsync(Root + "/retry", request, Token);
        await ProblemAssert.HasStatusAsync(noCsrf, HttpStatusCode.Forbidden, Token);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        foreach (var invalid in new[] { request with { Confirmed = false }, request with { ExpectedEffectiveRevision = 0 }, request with { ExpectedPlanHash = "invalid" } })
        {
            using var response = await client.PostJsonAsync(Root + "/retry", invalid, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, Token);
        }
        foreach (var stale in new[] { request with { ExpectedEffectiveRevision = 2 }, request with { ExpectedPlanHash = "sha256:" + new string('0', 64) } })
        {
            using var response = await client.PostJsonAsync(Root + "/retry", stale, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token);
        }
        using var spoofed = await client.PostJsonAsync(Root + "/retry", new { request.ExpectedEffectiveRevision, request.ExpectedPlanHash, confirmed = true, account = "forged" }, Token);
        await ProblemAssert.HasStatusAsync(spoofed, HttpStatusCode.BadRequest, Token);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
        Assert.Equal(3, versions.Load(1)!.AbnormalExitCount);
        Assert.Null(versions.ReadPointers().RunningRevision);
    }

    [Fact]
    public async Task AuditFailureRollsBackResetAndMarkerThenExplicitRepairCanRetry()
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var request = Request(await client.GetJsonAsync(Root, Token));
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_retry BEFORE INSERT ON audit_log WHEN NEW.action = 'runtime.retry' BEGIN SELECT RAISE(ABORT, 'private-storage-detail'); END");
        try
        {
            using var response = await client.PostJsonAsync(Root + "/retry", request, Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = (await response.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            var terminal = await client.GetJsonAsync(response.Headers.Location!.OriginalString, Token);
            Assert.Equal("failed", terminal.GetProperty("stage").GetString());
            Assert.DoesNotContain("private-storage-detail", terminal.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(3, versions.Load(1)!.AbnormalExitCount);
            Assert.Null(versions.ReadPointers().RunningRevision);
            Assert.Null(coordinator.Runtime.ActiveRevision);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_retry"); }
        using var repaired = await client.PostJsonAsync(Root + "/retry", request, Token);
        Assert.Equal(HttpStatusCode.Accepted, repaired.StatusCode);
        await OutputPipelineTests.UntilAsync(() => coordinator.Status.State == RuntimeCoordinatorState.Active);
        Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.RuntimeRetried));
        Assert.Single(versions.ReadHistory());
    }

    [Fact]
    public async Task RetrySharesApplicationOwnershipAndShutdownWaitsForOwnedPreparation()
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        var probe = new BlockingFactory(new RuntimeStateReader(data.Path));
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
            services.AddSingleton<IInputUnitFactory>(probe));
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var hash = RuntimeSnapshotDocument.ReadPlan(versions.Load(1)!).ContentHash;
        var operation = coordinator.BeginRetry(1, hash, "operator", null, Token);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(1, probe.Marker);
            Assert.Equal(0, versions.Load(1)!.AbnormalExitCount);
            Assert.Equal(0, probe.Started);
            Assert.Equal(operation.Id, Assert.Throws<RuntimeApplicationBusyException>(() => coordinator.BeginRetry(1, hash, "other", null, Token)).Application.Id);
            await Assert.ThrowsAsync<RuntimeApplicationBusyException>(() => coordinator.ApplyDraftAsync("0", "other", null, Token));
            await Assert.ThrowsAsync<RuntimeApplicationBusyException>(() => coordinator.RollbackAsync(1, "0", "other", null, Token));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => coordinator.BeginRetry(1, hash, "other", null, cancelled.Token));
            var stopping = coordinator.StopAsync(Token);
            Assert.False(stopping.IsCompleted);
            probe.Release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(RuntimeCoordinatorState.Stopped, coordinator.Status.State);
            Assert.Null(versions.ReadPointers().RunningRevision);
            Assert.Single(coordinator.ReadApplications());
            Assert.Single(versions.ReadHistory());
        }
        finally { probe.Release.Set(); }
    }

    private static ConfigurationRetryRequest Request(System.Text.Json.JsonElement state) =>
        new(state.GetProperty("effectiveRevision").GetInt64(), state.GetProperty("planHash").GetString()!, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeStateAndRetryShareTheExistingPreviewBudgetWithoutResettingAnything(bool global)
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var request = Request(await client.GetJsonAsync(Root, Token));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = Enumerable.Range(0, global ? 4 : 2).Select(i => budget.TryAcquire(global ? "other" + i : key)!).ToArray();
        try
        {
            using var read = await client.GetAsync(Root, Token);
            await ProblemAssert.HasStatusAsync(read, HttpStatusCode.TooManyRequests, Token);
            using var retry = await client.PostJsonAsync(Root + "/retry", request, Token);
            await ProblemAssert.HasStatusAsync(retry, HttpStatusCode.TooManyRequests, Token);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
        Assert.Equal(3, factory.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.AbnormalExitCount);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.RuntimeRetried));
    }

    [Fact]
    public async Task RealKestrelBoundsTheRetryRequestBeforeBinding()
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
        using var response = await client.PostAsync(new Uri(Root + "/retry", UriKind.Relative), body, Token);
        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.RequestEntityTooLarge, Token);
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("schema")]
    [InlineData("effective")]
    [InlineData("pending")]
    public async Task ResetRechecksTheCapturedCheckpointBeforeWritingCounterMarkerOrAudit(string change)
    {
        using var data = TemporaryDirectory.Create();
        await InitializeAsync(data.Path);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var hash = RuntimeSnapshotDocument.ReadPlan(versions.Load(1)!).ContentHash;
        var checkpoint = versions.ReadRetryCheckpoint(1, hash);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand(change switch
        {
            "content" => "UPDATE runtime_snapshot SET content = '{}' WHERE revision = 1",
            "schema" => "UPDATE runtime_snapshot SET schema_version = 99 WHERE revision = 1",
            "effective" => "UPDATE system_state SET effective_revision = NULL",
            _ => "UPDATE system_state SET pending_revision = 2",
        });
        var error = Assert.Throws<DraftWriteException>(() => versions.MarkRetry(checkpoint, "operator", null));
        Assert.Equal(change == "pending" ? "runtime.applicationInProgress" : "configuration.reviewChanged", error.Code);
        Assert.Equal(3, versions.Load(1)!.AbnormalExitCount);
        Assert.Null(versions.ReadPointers().RunningRevision);
        Assert.Empty(TestAccounts.Audit(factory.Services, AuditActions.RuntimeRetried));
    }

    private static async Task InitializeAsync(string directory, UserRole role = UserRole.Configurator)
    {
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory))
        {
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, role);
            var plan = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/sample","configuration":{"periodMs":1000,"seed":42}},{"kind":"input.testretry","key":"probe/sample","configuration":{}}]}"""u8);
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "fixture"), null);
            factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "unapplied", "Unapplied device", DeviceKind.Simulator), "author", null);
        }
        new RuntimeStateReader(directory).Execute("UPDATE runtime_snapshot SET abnormal_exit_count = 3 WHERE revision = 1");
    }

    private sealed class BlockingFactory(RuntimeStateReader state) : IInputUnitFactory
    {
        public string Kind => "input.testretry";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public long? Marker { get; private set; }
        public int Started;
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            Marker = state.Pointers().Running;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10), Token))
            { throw new TimeoutException("test-preparation-gate"); }
            return new Unit(unit.Id, this);
        }
        private sealed class Unit(RuntimeUnitId id, BlockingFactory owner) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public Task RunAsync(CancellationToken stoppingToken)
            { Interlocked.Increment(ref owner.Started); return Task.Delay(Timeout.Infinite, stoppingToken); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
