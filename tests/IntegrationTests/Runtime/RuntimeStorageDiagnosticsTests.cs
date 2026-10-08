using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeStorageDiagnosticsTests
{
    private const string Marker = "artificial-sensitive-storage-marker";

    [Theory]
    [InlineData("startup")]
    [InlineData("stable")]
    [InlineData("shutdown")]
    public async Task VersionStorageErrorsDoNotExposeDatabaseExceptionDetails(string phase)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var logs = new LogCapture();
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllTextAsync(seed, """{"schemaVersion":1,"units":[]}""", TestContext.Current.CancellationToken);
        await using (var initial = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed }))
        {
            Assert.Equal(RuntimeCoordinatorState.Active, initial.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        }
        var state = new RuntimeStateReader(data.Path);
        Assert.Null(state.Pointers().Running);
        var trigger = phase switch
        {
            "startup" => "BEFORE UPDATE OF running_revision ON system_state WHEN NEW.running_revision IS NOT NULL",
            "stable" => "BEFORE UPDATE OF is_stable ON runtime_snapshot WHEN NEW.is_stable = 1",
            _ => "BEFORE UPDATE OF running_revision ON system_state WHEN NEW.running_revision IS NULL",
        };
        state.Execute($"CREATE TRIGGER fail_version_diagnostic {trigger} BEGIN SELECT RAISE(ABORT, '{Marker}'); END;");
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.StableAfterSecondsKey] = "1" },
            services => services.AddLogging(logging => logging.AddProvider(logs)));
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        if (phase == "startup")
        {
            Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
            Assert.DoesNotContain(Marker, coordinator.Status.Reason ?? "");
            await state.WaitUntilAsync(reader => reader.Events(RuntimeEventTypes.RunFailed).Count == 1,
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Marker, Assert.Single(state.Events(RuntimeEventTypes.RunFailed)).Message);
            Assert.Null(state.Pointers().Running);
        }
        else
        {
            Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
            if (phase == "stable")
            {
                await state.WaitUntilAsync(_ => logs.Entries.Any(entry => entry.Message.Contains("记为已稳定", StringComparison.Ordinal)
                    && entry.Level == LogLevel.Warning), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.False(state.Snapshot(1).IsStable);
                Assert.Equal(1, state.Pointers().Running);
            }
            else
            {
                await host.DisposeAsync();
                Assert.Equal(1, state.Pointers().Running);
                Assert.Contains(logs.Entries, entry => entry.Message.Contains("未能清除", StringComparison.Ordinal));
            }
        }
        var coordinatorLogs = logs.Entries.Where(entry => entry.Category == typeof(RuntimeCoordinator).FullName).ToArray();
        Assert.NotEmpty(coordinatorLogs);
        Assert.All(coordinatorLogs, entry =>
        {
            Assert.DoesNotContain(Marker, entry.Message);
            Assert.Null(entry.ExceptionText);
        });
    }
}
