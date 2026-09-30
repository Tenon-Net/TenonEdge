using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 统一错误响应：使用正确的状态码与 ProblemDetails，不以 200 包装失败，不泄露内部细节（后端实施规范 §3.1）。
/// </summary>
public sealed class ErrorResponseTests(TenonEdgeApplicationFactory factory) : IClassFixture<TenonEdgeApplicationFactory>
{
    [Theory]
    [InlineData("/does-not-exist")]
    [InlineData("/api/edge-admin/v1/does-not-exist")]
    public async Task UnknownPathReturnsNotFoundProblem(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.NotFound, cancellationToken);
    }

    [Fact]
    public async Task UnsupportedMethodReturnsMethodNotAllowedProblem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/healthz", UriKind.Relative), content: null, cancellationToken);

        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.MethodNotAllowed, cancellationToken);
        Assert.Equal([HttpMethods.Get], response.Content.Headers.Allow);
    }

    [Fact]
    public async Task UnhandledExceptionReturnsProblemWithoutInternalDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<HealthCheckService, ThrowingHealthCheckService>()));
        using var client = failing.CreateClient();

        using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellationToken);

        var body = await ProblemAssert.HasStatusAsync(response, HttpStatusCode.InternalServerError, cancellationToken);
        Assert.DoesNotContain(ThrowingHealthCheckService.Detail, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ThrowingHealthCheckService), body, StringComparison.Ordinal);
    }

    /// <summary>模拟请求处理中出现未预期的异常。</summary>
    private sealed class ThrowingHealthCheckService : HealthCheckService
    {
        public const string Detail = "internal-detail-7f3c";

        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Detail);
    }
}
