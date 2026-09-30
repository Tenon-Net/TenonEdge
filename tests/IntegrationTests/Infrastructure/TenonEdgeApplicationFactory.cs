using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 在进程内启动实际的宿主装配（Program）。
/// WebApplicationFactory 默认使用 Development 环境；这里默认改为 Production，
/// 使错误处理、匿名例外等行为与正式运行一致。
/// </summary>
public class TenonEdgeApplicationFactory : WebApplicationFactory<Program>
{
    protected virtual string EnvironmentName => Environments.Production;

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(EnvironmentName);
}

/// <summary>以 Development 环境启动，用于核对只在开发环境开放的行为。</summary>
public sealed class DevelopmentApplicationFactory : TenonEdgeApplicationFactory
{
    protected override string EnvironmentName => Environments.Development;
}
