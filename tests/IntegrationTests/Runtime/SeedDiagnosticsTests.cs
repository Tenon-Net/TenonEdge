using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class SeedDiagnosticsTests
{
    private const string Marker = "artificial-sensitive-seed-marker";

    [Theory]
    [InlineData("""{"schemaVersion":1,"units":[],"MARKER":"rejected"}""")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/sim","configuration":{},"MARKER":true}]}""")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"output.mqtt","key":"out","configuration":{"MARKER":"rejected"}}]}""")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.http","key":"meter/http","configuration":{"MARKER":"rejected"}}]}""")]
    public async Task RejectedSeedDoesNotPersistUntrustedParserDetails(string template)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var logs = new LogCapture();
        var path = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllTextAsync(path, template.Replace("MARKER", Marker, StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = path },
            services => services.AddLogging(logging => logging.AddProvider(logs)));
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.Equal(RuntimeCoordinatorState.Idle, coordinator.Status.State);
        var state = new RuntimeStateReader(data.Path);
        Assert.Empty(state.Snapshots());
        Assert.Null(state.Pointers().Effective);
        await state.WaitUntilAsync(reader => reader.Events(RuntimeEventTypes.SeedRejected).Count == 1,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var rejected = Assert.Single(state.Events(RuntimeEventTypes.SeedRejected));
        Assert.DoesNotContain(Marker, rejected.Message);
        Assert.DoesNotContain(Marker, rejected.Target ?? "");
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(Marker, entry.Message);
            Assert.DoesNotContain(Marker, entry.ExceptionText ?? "");
        });
    }
}
