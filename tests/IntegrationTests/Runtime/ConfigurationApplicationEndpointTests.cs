using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationApplicationEndpointTests
{
    private const string ApplyPath = "/api/edge-admin/v1/configuration/apply";
    private const string RollbackPath = "/api/edge-admin/v1/configuration/versions/1/rollback";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", false)]
    [InlineData("Viewer", true)]
    [InlineData("Configurator", false)]
    [InlineData("Configurator", true)]
    [InlineData("Administrator", false)]
    [InlineData("Administrator", true)]
    public async Task AuthorizedConfirmationReturnsAnOwnedOperationAndEventuallyPersistsTheVersionAndAudit(string role, bool rollback)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        if (rollback)
        { await coordinator.ApplyDraftAsync("0", "seed", null, Token); }
        var review = rollback ? versions.ReviewRollback(1, "0", Token) : versions.ReviewDraft("0", Token);
        var request = Request(review) with { ChangeDescription = "Confirmed through API" };
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(rollback ? RollbackPath : ApplyPath, request, Token);
        if (role == "Viewer")
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Forbidden, Token);
            Assert.Equal(rollback ? 1 : 0, versions.ReadHistory().Length);
            return;
        }
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var accepted = await response.ReadJsonAsync(Token);
        var id = accepted.GetProperty("id").GetGuid();
        Assert.Equal("operator", accepted.GetProperty("account").GetString());
        Assert.Equal($"/api/edge-admin/v1/configuration/operations/{id}", response.Headers.Location!.OriginalString);
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
        var terminal = await client.GetJsonAsync(response.Headers.Location.OriginalString, Token);
        Assert.Equal("completed", terminal.GetProperty("stage").GetString());
        Assert.True(terminal.GetProperty("committed").GetBoolean());
        var revision = terminal.GetProperty("revision").GetInt64();
        Assert.Equal(rollback ? 2 : 1, revision);
        Assert.Equal("Confirmed through API", versions.Load(revision)!.ChangeDescription);
        var audit = Assert.Single(TestAccounts.Audit(factory.Services, rollback ? "runtime.rollback" : "runtime.apply"), entry => entry.AccountName == "operator");
        Assert.Equal("r" + revision, audit.Target);
        // 成功后重复发送同一确认不能再生成版本（即使业务草稿为空且没有修改）。
        using var repeated = await client.PostJsonAsync(rollback ? RollbackPath : ApplyPath, request, Token);
        await ProblemAssert.HasStatusAsync(repeated, HttpStatusCode.Conflict, Token);
        Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndMissingCsrfCannotStartOperations(bool rollback)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var request = Request(factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft("0", Token));
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(rollback ? RollbackPath : ApplyPath, request, Token);
        await ProblemAssert.HasStatusAsync(denied, HttpStatusCode.Unauthorized, Token);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(rollback ? RollbackPath : ApplyPath, request, Token);
        await ProblemAssert.HasStatusAsync(csrf, HttpStatusCode.Forbidden, Token);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task MissingConfirmationInvalidDescriptionsAndChangedReviewCannotAllocateVersions()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var request = Request(versions.ReviewDraft("0", Token));
        foreach (var invalid in new[] { request with { Confirmed = false }, request with { ExpectedPlanHash = "invalid" },
            request with { ChangeDescription = new string('x', 1001) }, request with { ChangeDescription = "invalid\0note" } })
        {
            using var response = await client.PostJsonAsync(ApplyPath, invalid, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.BadRequest, Token);
        }
        foreach (var stale in new[] { request with { ExpectedVersion = "stale" }, request with { ExpectedEffectiveRevision = 99 },
            request with { ExpectedPlanHash = "sha256:" + new string('0', 64) } })
        {
            using var response = await client.PostJsonAsync(ApplyPath, stale, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token);
        }
        using var spoofed = await client.PostJsonAsync(ApplyPath, new { request.ExpectedVersion, request.ExpectedPlanHash, request.ExpectedEffectiveRevision, confirmed = true, account = "forged" }, Token);
        await ProblemAssert.HasStatusAsync(spoofed, HttpStatusCode.BadRequest, Token);
        Assert.Empty(versions.ReadHistory());
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task InvalidEnabledRouteBlocksApplicationBeforeAdmission()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var route = factory.Services.GetRequiredService<RouteDraftStore>().Write(null,
            new("0", "broken", "Broken", true, new(1, [], [], new([]))), "operator", null);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(ApplyPath, new ConfigurationApplicationRequest(route.Version, null, "sha256:" + new string('0', 64), true), Token);
        Assert.Contains("configuration.preflightFailed", await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token), StringComparison.Ordinal);
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
        Assert.Empty(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationSharesThePreviewBudget(bool global)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var request = Request(factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft("0", Token));
        var leases = Enumerable.Range(0, global ? 4 : 2).Select(i => budget.TryAcquire(global ? "other" + i : key)!).ToArray();
        try
        {
            foreach (var path in new[] { ApplyPath, RollbackPath })
            {
                using var response = await client.PostJsonAsync(path, request, Token);
                await ProblemAssert.HasStatusAsync(response, HttpStatusCode.TooManyRequests, Token);
            }
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        Assert.Empty(factory.Services.GetRequiredService<RuntimeCoordinator>().ReadApplications());
    }

    [Fact]
    public async Task AcceptedOperationPublishesSafeFailureWhenStorageFailsAfterAdmission()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var request = Request(factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft("0", Token));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER reject_application BEFORE INSERT ON runtime_snapshot BEGIN SELECT RAISE(ABORT, 'private-storage-detail'); END");
        try
        {
            using var response = await client.PostJsonAsync(ApplyPath, request, Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var id = (await response.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            var terminal = await client.GetJsonAsync(response.Headers.Location!.OriginalString, Token);
            Assert.Equal("failed", terminal.GetProperty("stage").GetString());
            Assert.Equal("runtime.applicationFailed", terminal.GetProperty("errorCode").GetString());
            Assert.DoesNotContain("private-storage-detail", terminal.GetRawText(), StringComparison.Ordinal);
            Assert.Empty(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER reject_application"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreezeRechecksTheConfirmedEffectiveRevisionAndPlanAfterAdmission(bool wrongHash)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var review = versions.ReviewDraft("0", Token);
        await coordinator.ApplyDraftAsync("0", "other", null, Token);
        var expectation = wrongHash ? new RuntimeApplicationExpectation(1, "sha256:" + new string('0', 64))
            : new RuntimeApplicationExpectation(review.EffectiveRevision, review.PlanHash!);
        var operation = coordinator.BeginApplyDraft("0", expectation, "operator", null, null, Token);
        await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(operation.Id)?.FinishedAt is not null);
        Assert.Equal("configuration.reviewChanged", coordinator.ReadApplication(operation.Id)!.ErrorCode);
        Assert.Single(versions.ReadHistory());

    }

    [Fact]
    public async Task RealKestrelBoundsBothApplicationBodiesBeforeBinding()
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
        foreach (var path in new[] { ApplyPath, RollbackPath })
        {
            using var body = new StringContent(new string(' ', 8192) + "{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(path, UriKind.Relative), body, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.RequestEntityTooLarge, Token);
        }
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

    internal static ConfigurationApplicationRequest Request(ConfigurationReview review) =>
        new(review.Version, review.EffectiveRevision, review.PlanHash!, true);
}
