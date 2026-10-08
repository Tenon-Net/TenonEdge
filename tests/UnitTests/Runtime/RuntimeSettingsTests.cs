using Microsoft.Extensions.Configuration;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>运行时的部署参数（T1-01）：种子快照须为绝对路径；稳定时间默认 60 秒，范围 1—86400 秒。</summary>
public sealed class RuntimeSettingsTests
{
    [Fact]
    public void DefaultsHaveNoSeedAndSixtySecondsStableTime()
    {
        var settings = RuntimeSettings.Resolve(Configuration());

        Assert.Null(settings.SeedSnapshotPath);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.StableAfter);
    }

    [Fact]
    public void ValidValuesAreUsed()
    {
        var path = Path.Combine(Path.GetTempPath(), "seed.json");

        var settings = RuntimeSettings.Resolve(Configuration((RuntimeSettings.SeedSnapshotKey, path), (RuntimeSettings.StableAfterSecondsKey, "5")));

        Assert.Equal(path, settings.SeedSnapshotPath);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.StableAfter);
    }

    [Theory]
    [InlineData(RuntimeSettings.SeedSnapshotKey, "seed.json")]
    [InlineData(RuntimeSettings.StableAfterSecondsKey, "0")]
    [InlineData(RuntimeSettings.StableAfterSecondsKey, "86401")]
    [InlineData(RuntimeSettings.StableAfterSecondsKey, "1.5")]
    [InlineData(RuntimeSettings.StableAfterSecondsKey, "-5")]
    public void InvalidValuesAreDeploymentErrors(string key, string value)
    {
        var exception = Assert.Throws<HostStartupException>(() => RuntimeSettings.Resolve(Configuration((key, value))));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
}
