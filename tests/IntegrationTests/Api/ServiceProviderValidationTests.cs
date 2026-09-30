using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 宿主在生产环境配置下同样开启作用域与构建时校验（后端实施规范 §4，BE-AT-03 基础）。
/// </summary>
public sealed class ServiceProviderValidationTests
{
    [Fact]
    public void ScopedServiceCannotBeResolvedFromRootProvider()
    {
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ScopedProbe>()));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services.GetRequiredService<ScopedProbe>());

        Assert.Contains(nameof(ScopedProbe), exception.Message, StringComparison.Ordinal);
        Assert.Contains("root provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDependencyFailsWhenHostIsBuilt()
    {
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<NeedsUnregisteredDependency>()));

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains(nameof(UnregisteredDependency), exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class ScopedProbe;

    private sealed class UnregisteredDependency;

    private sealed class NeedsUnregisteredDependency(UnregisteredDependency dependency)
    {
        public UnregisteredDependency Dependency { get; } = dependency;
    }
}
