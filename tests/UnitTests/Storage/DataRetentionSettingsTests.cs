using Microsoft.Extensions.Configuration;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Storage;

/// <summary>
/// 审计保留期（总设计 §3.3、功能需求 §9.2）：默认 180 天，可调长，不能短于 180 天（网络日志留存不少于六个月），最长 36,500 天；
/// 取值无效时宿主以配置错误的退出码拒绝启动。
/// </summary>
public sealed class DataRetentionSettingsTests
{
    [Fact]
    public void AuditRetentionDefaultsTo180Days()
    {
        var settings = DataRetentionSettings.Resolve(Configuration(null));

        Assert.Equal(TimeSpan.FromDays(180), settings.AuditRetention);
    }

    [Theory]
    [InlineData("180", 180)]
    [InlineData("400", 400)]
    [InlineData("36500", 36_500)]
    public void AuditRetentionCanBeLengthened(string value, int expectedDays)
    {
        var settings = DataRetentionSettings.Resolve(Configuration(value));

        Assert.Equal(TimeSpan.FromDays(expectedDays), settings.AuditRetention);
    }

    [Theory]
    [InlineData("179")]
    [InlineData("0")]
    [InlineData("-200")]
    [InlineData("180.5")]
    [InlineData(" 200")]
    [InlineData("half-year")]
    [InlineData("36501")]
    [InlineData("99999999")]
    public void ShorterOrInvalidAuditRetentionIsAConfigurationError(string value)
    {
        var exception = Assert.Throws<HostStartupException>(() => DataRetentionSettings.Resolve(Configuration(value)));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains(DataRetentionSettings.AuditRetentionDaysKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEventLimitsFollowTheDesign()
    {
        Assert.Equal(TimeSpan.FromDays(7), DataRetentionSettings.RuntimeEventRetention);
        Assert.Equal(10_000, DataRetentionSettings.MaximumRuntimeEvents);
    }

    private static IConfiguration Configuration(string? auditRetentionDays) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataRetentionSettings.AuditRetentionDaysKey] = auditRetentionDays })
            .Build();
}
