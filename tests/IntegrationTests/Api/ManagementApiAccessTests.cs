using System.Net;
using System.Net.Mime;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 管理入口默认受保护，匿名例外逐项列出（后端实施规范 §3.2）。
/// 真实登录与完整的 401/403 场景在认证任务中补充；这里只验证尚无登录能力时的默认保护。
/// </summary>
public sealed class ManagementApiAccessTests(
    TenonEdgeApplicationFactory production,
    DevelopmentApplicationFactory development)
    : IClassFixture<TenonEdgeApplicationFactory>, IClassFixture<DevelopmentApplicationFactory>
{
    private const string ApiDocumentPath = "/api/edge-admin/v1/openapi/v1.json";

    [Fact]
    public void ProductionMapsOnlyExpectedEndpointsAndOnlyHealthChecksAreAnonymous()
    {
        Assert.Equal(
            [
                "GET /api/edge-admin/v1/openapi/{documentName}.json (protected)",
                "GET /healthz (anonymous)",
                "GET /readyz (anonymous)",
            ],
            EndpointInventory.Describe(production.Services));
    }

    [Fact]
    public void DevelopmentOnlyAddsAnonymousAccessToApiDocument()
    {
        Assert.Equal(
            [
                "GET /api/edge-admin/v1/openapi/{documentName}.json (anonymous)",
                "GET /healthz (anonymous)",
                "GET /readyz (anonymous)",
            ],
            EndpointInventory.Describe(development.Services));
    }

    [Fact]
    public async Task ApiDocumentRequiresAuthenticationOutsideDevelopment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = production.CreateClient();

        using var response = await client.GetAsync(new Uri(ApiDocumentPath, UriKind.Relative), cancellationToken);

        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, cancellationToken);
    }

    [Fact]
    public async Task ApiDocumentIsServedAnonymouslyInDevelopment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = development.CreateClient();

        using var response = await client.GetAsync(new Uri(ApiDocumentPath, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaTypeNames.Application.Json, response.Content.Headers.ContentType?.MediaType);
    }
}
