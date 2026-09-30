using System.Net;
using System.Net.Mime;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TenonEdge.Host.Features.Health;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// /healthz 与 /readyz（总设计 §10.1）：匿名、只返回状态、与实际就绪条件一致。
/// </summary>
public sealed class HealthEndpointTests(TenonEdgeApplicationFactory factory) : IClassFixture<TenonEdgeApplicationFactory>
{
    private const string SecretReason = "readiness-detail-5b1e";

    [Theory]
    [InlineData(HealthEndpoints.LivenessPath)]
    [InlineData(HealthEndpoints.ReadinessPath)]
    public async Task StartedHostReportsHealthyPlainTextWithoutCaching(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaTypeNames.Text.Plain, response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public void ReadinessConditionsIncludeHostLifecycle()
    {
        // 生命周期条件本身的语义（未启动完成、已开始停机时不就绪）由单元测试覆盖；这里确认宿主确实把它登记为就绪条件。
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        using var scope = factory.Services.CreateScope();

        Assert.Contains(
            registrations,
            registration => registration.Tags.Contains(HealthEndpoints.ReadinessTag)
                && registration.Factory(scope.ServiceProvider) is HostLifecycleReadinessCheck);
    }

    [Fact]
    public void ReadinessConditionsIncludeRuntimeStartConditions()
    {
        // 阻止原因登记后返回 503 的行为见 ConfigStoreStartupTests；这里确认宿主把它登记为就绪条件。
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        using var scope = factory.Services.CreateScope();

        Assert.Contains(
            registrations,
            registration => registration.Tags.Contains(HealthEndpoints.ReadinessTag)
                && registration.Factory(scope.ServiceProvider) is RuntimeStartConditionsReadinessCheck);
    }

    [Fact]
    public async Task FailingReadinessConditionReturnsServiceUnavailableWithoutReason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var notReady = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHealthChecks().AddCheck(
                "test-not-ready",
                () => HealthCheckResult.Unhealthy(SecretReason),
                tags: [HealthEndpoints.ReadinessTag])));
        using var client = notReady.CreateClient();

        using var readiness = await client.GetAsync(new Uri(HealthEndpoints.ReadinessPath, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("Unhealthy", await readiness.Content.ReadAsStringAsync(cancellationToken));

        // 存活检查只表示进程能处理请求，不受就绪条件影响。
        using var liveness = await client.GetAsync(new Uri(HealthEndpoints.LivenessPath, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task ReadinessOnlyEvaluatesChecksTaggedForReadiness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var withUnrelatedCheck = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHealthChecks().AddCheck("test-unrelated", () => HealthCheckResult.Unhealthy())));
        using var client = withUnrelatedCheck.CreateClient();

        using var readiness = await client.GetAsync(new Uri(HealthEndpoints.ReadinessPath, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
    }
}
