using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 宿主启动时的配置库初始化（T0-05）与运行时启动条件：启动完成时配置库已迁移到本程序的最新版本；
/// 存在阻止运行时启动的原因时，/readyz 返回 503。
/// </summary>
public sealed class ConfigStoreStartupTests
{
    [Fact]
    public void StartedHostHasConfigStoreAtLatestSchemaVersion()
    {
        using var factory = new TenonEdgeApplicationFactory();

        var database = factory.Services.GetRequiredService<ConfigDatabase>();

        Assert.True(File.Exists(database.FilePath));
        Assert.Equal(
            ConfigMigrations.All[^1].Version,
            database.Client.Ado.GetInt($"SELECT MAX(version) FROM {SchemaMigrationRecord.TableName}"));
        Assert.True(factory.Services.GetRequiredService<RuntimeStartConditions>().AllowsRuntimeStart);
    }

    [Fact]
    public async Task BlockedRuntimeStartMakesReadinessUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        using (var ready = await client.GetAsync(new Uri(HealthEndpoints.ReadinessPath, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        factory.Services.GetRequiredService<RuntimeStartConditions>().Block("test-blocker", "测试登记的阻止原因");

        using var readiness = await client.GetAsync(new Uri(HealthEndpoints.ReadinessPath, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("Unhealthy", await readiness.Content.ReadAsStringAsync(cancellationToken));

        // 存活检查只表示进程能处理请求，不受运行时启动条件影响。
        using var liveness = await client.GetAsync(new Uri(HealthEndpoints.LivenessPath, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }
}
