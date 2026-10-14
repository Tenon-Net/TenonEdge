using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.IntegrationTests.Deliveries;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Auth;

public sealed partial class ApiTokenTests
{
    [Fact]
    public async Task BearerCanReviewAndApplyConfigurationAndTheOwnedCommitPreservesItsIdentity()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var created = await Create(admin, "MES apply", "configurator");
        using var script = Script(factory, created.Secret);
        using var reviewed = await script.PostJsonAsync("/api/edge-admin/v1/configuration/review", new { expectedVersion = "0" }, Token);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var review = await reviewed.ReadJsonAsync(Token);
        using var applied = await script.PostJsonAsync("/api/edge-admin/v1/configuration/apply", new
        {
            expectedVersion = "0",
            expectedEffectiveRevision = (long?)null,
            expectedPlanHash = review.GetProperty("planHash").GetString(),
            confirmed = true,
        }, Token);
        Assert.Equal(HttpStatusCode.Accepted, applied.StatusCode);
        await DeliveryExportApiTests.WaitUntilAsync(() => TestAccounts.Audit(factory.Services, AuditActions.SnapshotApplied).Count == 1);
        Assert.Equal("MES apply", Assert.Single(TestAccounts.Audit(factory.Services, AuditActions.SnapshotApplied)).TokenName);
    }

    [Fact]
    public async Task TwoTokensFromTheSameAccountCannotShareExportResourcesAndDownloadIsAuditedToTheOwner()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var first = await Create(admin, "MES export A", "administrator");
        var second = await Create(admin, "MES export B", "administrator");
        using var owner = Script(factory, first.Secret);
        using var other = Script(factory, second.Secret);
        await DeliveryExportApiTests.SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Failed);
        const string export = "/api/edge-admin/v1/deliveries/export";
        using var prepared = await owner.PostJsonAsync(export, new { state = "failed" }, Token);
        Assert.Equal(HttpStatusCode.Accepted, prepared.StatusCode);
        var id = (await prepared.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        await DeliveryExportApiTests.WaitAsync(owner, "awaitingConfirmation");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await other.GetJsonAsync(export, Token)).GetProperty("export").ValueKind);
        using var stolen = await other.PostJsonAsync($"{export}/{id}/confirm", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        using var confirmed = await owner.PostJsonAsync($"{export}/{id}/confirm", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        await DeliveryExportApiTests.WaitAsync(owner, "ready");
        using var otherDownload = await other.GetAsync($"{export}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.NotFound, otherDownload.StatusCode);
        using var downloaded = await owner.GetAsync($"{export}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.NotEmpty(await downloaded.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal("MES export A", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export")).TokenName);
        await DeliveryExportApiTests.WaitUntilAsync(() => TestAccounts.Audit(factory.Services, "delivery.export.download").Count == 1);
        Assert.Equal("MES export A", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export.download")).TokenName);
    }
}
