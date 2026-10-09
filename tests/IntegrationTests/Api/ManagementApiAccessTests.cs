using System.Net;
using System.Net.Mime;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 管理入口默认受保护，匿名例外逐项列出（后端实施规范 §3.2）：健康检查、首次设置状态与提交、登录。
/// 新增端点须同时更新清单并说明访问级别；登录后的访问与 401/403 场景见认证相关测试。
/// </summary>
public sealed class ManagementApiAccessTests(
    TenonEdgeApplicationFactory production,
    DevelopmentApplicationFactory development)
    : IClassFixture<TenonEdgeApplicationFactory>, IClassFixture<DevelopmentApplicationFactory>
{
    private const string ApiDocumentPath = "/api/edge-admin/v1/openapi/v1.json";

    [Fact]
    public void ProductionMapsOnlyExpectedEndpointsAndOnlyListedExceptionsAreAnonymous()
    {
        Assert.Equal(
            [
                "DELETE /api/edge-admin/v1/connections/{connectionId:long} (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/devices/{deviceId:long} (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/devices/{deviceId:long}/inputs/{inputId:long} (protected: role:configurator)",
                "GET /api/edge-admin/v1/audit (protected: role:administrator)",
                "GET /api/edge-admin/v1/auth/session (protected)",
                "GET /api/edge-admin/v1/connections/ (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/delivery-control (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected)",
                "GET /api/edge-admin/v1/devices/ (protected)",
                "GET /api/edge-admin/v1/devices/{deviceId:long}/inputs/ (protected)",
                "GET /api/edge-admin/v1/devices/{deviceId:long}/points/csv/ (protected: role:configurator)",
                "GET /api/edge-admin/v1/openapi/{documentName}.json (protected)",
                "GET /api/edge-admin/v1/runtime/events (protected)",
                "GET /api/edge-admin/v1/runtime/status (protected)",
                "GET /api/edge-admin/v1/runtime/trends (protected)",
                "GET /api/edge-admin/v1/setup (anonymous)",
                "GET /api/edge-admin/v1/system/info (protected)",
                "GET /api/edge-admin/v1/system/outbound-policy (protected)",
                "GET /healthz (anonymous)",
                "GET /readyz (anonymous)",
                "POST /api/edge-admin/v1/auth/login (anonymous)",
                "POST /api/edge-admin/v1/auth/logout (protected)",
                "POST /api/edge-admin/v1/auth/password (protected)",
                "POST /api/edge-admin/v1/connections/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/copy (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/inputs/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/points/csv/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/setup (anonymous)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/delivery-control (protected: role:administrator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected: role:configurator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected: role:configurator)",
                "PUT /api/edge-admin/v1/devices/{deviceId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/devices/{deviceId:long}/inputs/{inputId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/system/outbound-policy (protected: role:administrator)",
            ],
            EndpointInventory.Describe(production.Services));
    }

    [Fact]
    public void DevelopmentOnlyAddsAnonymousAccessToApiDocument()
    {
        Assert.Equal(
            [
                "DELETE /api/edge-admin/v1/connections/{connectionId:long} (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/devices/{deviceId:long} (protected: role:configurator)",
                "DELETE /api/edge-admin/v1/devices/{deviceId:long}/inputs/{inputId:long} (protected: role:configurator)",
                "GET /api/edge-admin/v1/audit (protected: role:administrator)",
                "GET /api/edge-admin/v1/auth/session (protected)",
                "GET /api/edge-admin/v1/connections/ (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/delivery-control (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected)",
                "GET /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected)",
                "GET /api/edge-admin/v1/devices/ (protected)",
                "GET /api/edge-admin/v1/devices/{deviceId:long}/inputs/ (protected)",
                "GET /api/edge-admin/v1/devices/{deviceId:long}/points/csv/ (protected: role:configurator)",
                "GET /api/edge-admin/v1/openapi/{documentName}.json (anonymous)",
                "GET /api/edge-admin/v1/runtime/events (protected)",
                "GET /api/edge-admin/v1/runtime/status (protected)",
                "GET /api/edge-admin/v1/runtime/trends (protected)",
                "GET /api/edge-admin/v1/setup (anonymous)",
                "GET /api/edge-admin/v1/system/info (protected)",
                "GET /api/edge-admin/v1/system/outbound-policy (protected)",
                "GET /healthz (anonymous)",
                "GET /readyz (anonymous)",
                "POST /api/edge-admin/v1/auth/login (anonymous)",
                "POST /api/edge-admin/v1/auth/logout (protected)",
                "POST /api/edge-admin/v1/auth/password (protected)",
                "POST /api/edge-admin/v1/connections/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/copy (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/inputs/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/devices/{deviceId:long}/points/csv/ (protected: role:configurator)",
                "POST /api/edge-admin/v1/setup (anonymous)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/delivery-control (protected: role:administrator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/http-credential (protected: role:configurator)",
                "PUT /api/edge-admin/v1/connections/{connectionId:long}/mqtt-credential (protected: role:configurator)",
                "PUT /api/edge-admin/v1/devices/{deviceId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/devices/{deviceId:long}/inputs/{inputId:long} (protected: role:configurator)",
                "PUT /api/edge-admin/v1/system/outbound-policy (protected: role:administrator)",
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
