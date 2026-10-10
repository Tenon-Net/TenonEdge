using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationReviewEndpointTests
{
    private const string DraftPath = "/api/edge-admin/v1/configuration/review";
    private const string RollbackPath = "/api/edge-admin/v1/configuration/versions/1/review";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", false, HttpStatusCode.Forbidden)]
    [InlineData("Viewer", true, HttpStatusCode.Forbidden)]
    [InlineData("Configurator", false, HttpStatusCode.OK)]
    [InlineData("Configurator", true, HttpStatusCode.OK)]
    [InlineData("Administrator", false, HttpStatusCode.OK)]
    [InlineData("Administrator", true, HttpStatusCode.OK)]
    public async Task ReviewsRequireConfigurationPermissionAndDoNotApplyOrRollback(string role, bool rollback, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        await coordinator.ApplyDraftAsync("0", "operator", null, Token);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var before = versions.ReadPointers();
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(rollback ? RollbackPath : DraftPath, new ConfigurationReviewRequest("0"), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True(response.Headers.CacheControl!.NoStore);
            var review = await response.ReadJsonAsync(Token);
            Assert.True(review.GetProperty("preflightPassed").GetBoolean());
            Assert.Equal(1, review.GetProperty("effectiveRevision").GetInt64());
            Assert.Equal("0", review.GetProperty("version").GetString());
        }
        Assert.Equal(before, versions.ReadPointers());
        Assert.Single(versions.ReadHistory());
        Assert.Single(coordinator.ReadApplications());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndMissingCsrfRequestsAreRejected(bool rollback)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var path = rollback ? RollbackPath : DraftPath;
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(path, new ConfigurationReviewRequest("0"), Token);
        await ProblemAssert.HasStatusAsync(denied, HttpStatusCode.Unauthorized, Token);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(path, new ConfigurationReviewRequest("0"), Token);
        await ProblemAssert.HasStatusAsync(csrf, HttpStatusCode.Forbidden, Token);
    }

    [Fact]
    public async Task RealKestrelEnforcesBothReviewBodyLimitsBeforeBinding()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程验证依赖 Linux 的 SIGTERM 与 flock。");
        using var working = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        await using var host = HostProcess.Start(working.Path, "Production", data.Path);
        var timeout = TimeSpan.FromSeconds(30);
        using var client = new HttpClient { BaseAddress = await host.WaitForListeningAddressAsync(timeout, Token), Timeout = timeout };
        await host.WaitUntilReadyAsync(client, timeout, Token);
        using var setup = await client.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        await client.UseSessionAsync(setup, Token);
        foreach (var path in new[] { DraftPath, RollbackPath })
        {
            using var body = new StringContent(new string(' ', 4096) + "{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(path, UriKind.Relative), body, Token);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.RequestEntityTooLarge, Token);
        }
        using var valid = await client.PostJsonAsync(DraftPath, new ConfigurationReviewRequest("0"), Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewsShareTheExistingBudgetAndReleaseItAfterValidationFailures(bool global)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = Enumerable.Range(0, global ? 4 : 2).Select(i => budget.TryAcquire(global ? "other" + i : key)!).ToArray();
        try
        {
            foreach (var path in new[] { DraftPath, RollbackPath })
            {
                using var busy = await client.PostJsonAsync(path, new ConfigurationReviewRequest("0"), Token);
                await ProblemAssert.HasStatusAsync(busy, HttpStatusCode.TooManyRequests, Token);
            }
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        for (var i = 0; i < 3; i++)
        {
            using var invalid = await client.PostJsonAsync(DraftPath, new ConfigurationReviewRequest(" "), Token);
            await ProblemAssert.HasStatusAsync(invalid, HttpStatusCode.BadRequest, Token);
        }
        using var success = await client.PostJsonAsync(DraftPath, new ConfigurationReviewRequest("0"), Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task MissingUnappliedAndCorruptVersionsHaveSafeDistinctProblems()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using (var none = await client.PostJsonAsync(RollbackPath, new ConfigurationReviewRequest("0"), Token))
        { Assert.Contains("configuration.noEffectiveVersion", await ProblemAssert.HasStatusAsync(none, HttpStatusCode.Conflict, Token), StringComparison.Ordinal); }
        await factory.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync("0", "operator", null, Token);
        using (var missing = await client.PostJsonAsync(RollbackPath.Replace("/1/", "/999/", StringComparison.Ordinal), new ConfigurationReviewRequest("0"), Token))
        { Assert.Contains("configuration.versionNotFound", await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token), StringComparison.Ordinal); }
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var failed = versions.FreezeDraft(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), "0", "operator");
        versions.Abandon(failed, "fixture");
        using (var rejected = await client.PostJsonAsync(RollbackPath.Replace("/1/", "/2/", StringComparison.Ordinal), new ConfigurationReviewRequest("0"), Token))
        { Assert.Contains("configuration.rollbackTargetNotEffective", await ProblemAssert.HasStatusAsync(rejected, HttpStatusCode.Conflict, Token), StringComparison.Ordinal); }
        using (var stale = await client.PostJsonAsync(DraftPath, new ConfigurationReviewRequest("stale"), Token))
        { Assert.Contains("configuration.draftConflict", await ProblemAssert.HasStatusAsync(stale, HttpStatusCode.Conflict, Token), StringComparison.Ordinal); }
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("UPDATE runtime_snapshot SET content_hash = 'private-corrupt-detail' WHERE revision = 1");
        using var corrupt = await client.PostJsonAsync(DraftPath, new ConfigurationReviewRequest("0"), Token);
        var body = await ProblemAssert.HasStatusAsync(corrupt, HttpStatusCode.Conflict, Token);
        Assert.Contains("configuration.invalidSnapshot", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-corrupt-detail", body, StringComparison.Ordinal);
    }
}
