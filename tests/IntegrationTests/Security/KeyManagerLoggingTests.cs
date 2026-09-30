using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Security;

/// <summary>
/// XmlKeyManager 的 Trace 日志含未加密的主密钥（T0-09 脱敏说明）：宿主登记的每个日志提供程序都在限制规则的清单中，
/// 部署参数中的日志规则（包括比完整类别名更长的通配符规则）不能把它调回 Trace。真实进程中的输出见 AuditAndLoggingProcessTests。
/// </summary>
public sealed class KeyManagerLoggingTests
{
    [Fact]
    public void EveryRegisteredLoggerProviderIsCoveredByTheKeyManagerRule()
    {
        using var factory = new TenonEdgeApplicationFactory();

        var providers = factory.Services.GetServices<ILoggerProvider>().Select(provider => provider.GetType()).ToList();

        Assert.NotEmpty(providers);
        Assert.All(providers, type => Assert.Contains(type, SecretProtection.KeyManagerLogProviders));
    }

    [Theory]
    [InlineData("Logging:LogLevel:{C}*{C}")]
    [InlineData("Logging:Console:LogLevel:Default")]
    [InlineData("Logging:Console:LogLevel:{C}*")]
    [InlineData("Logging:Console:LogLevel:{C}*{C}")]
    public void ConfigurationCannotEnableTraceForTheKeyManager(string keyPattern)
    {
        var key = keyPattern.Replace("{C}", SecretProtection.KeyManagerLogCategory, StringComparison.Ordinal);
        using var baseFactory = new TenonEdgeApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting(key, "Trace"));

        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(SecretProtection.KeyManagerLogCategory);

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
    }
}
