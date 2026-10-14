using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Audit;

public sealed class AuditExportLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AuditExportActor Actor => new("test-session", new AuditAccount("admin", "MES audit"), "127.0.0.1");
    private static string FilePath(string root) => Path.Combine(root, AuditExportFiles.DirectoryName, AuditExportFiles.FileName);
    private static readonly StorageSpace Plenty = new(10_000_000_000, 5_000_000_000);

    [Theory]
    [InlineData("unknown")]
    [InlineData("bytes")]
    [InlineData("fraction")]
    [InlineData("duringWrite")]
    [InlineData("io")]
    [InlineData("audit")]
    public async Task GenerationSpaceIoOrPersistentAuditFailureNeverMakesPartialCsvDownloadable(string fault)
    {
        using var data = TemporaryDirectory.Create();
        ConfigDatabase? database = null;
        var reads = 0;
        StorageSpace? Space()
        {
            var call = Interlocked.Increment(ref reads);
            if (fault == "io" && call == 2)
            { throw new IOException("fixture IO failure"); }
            if (fault == "audit" && call == 2)
            { database!.InTransaction(client => client.Ado.ExecuteCommand("CREATE TRIGGER reject_export BEFORE INSERT ON audit_log WHEN NEW.action='audit.export' BEGIN SELECT RAISE(ABORT, 'fixture audit failure'); END")); }
            return fault switch
            {
                "unknown" => null,
                "bytes" => new(1_000_000_000, 1),
                "fraction" => new(100_000_000_000, 600_000_000),
                "duringWrite" when call > 1 => new(1_000_000_000, 1),
                _ => Plenty
            };
        }
        await using var factory = Factory(data.Path, Space);
        database = factory.Services.GetRequiredService<ConfigDatabase>();
        AuditExportApiTests.Seed(factory.Services, 3);
        var manager = factory.Services.GetRequiredService<AuditExportManager>();
        var start = manager.Start(Actor, new(Action: "fixture.csv"));
        await WaitAsync(() => manager.GetCurrent(Actor.Owner) is { State: AuditExportState.Failed, Busy: false });
        Assert.False(File.Exists(FilePath(data.Path)));
        Assert.Throws<AuditExportRequestException>(() => manager.OpenDownload(start.Id, Actor.Owner));
        Assert.Equal(fault is "io" or "audit" ? "audit.exportGenerationFailed" : "audit.exportInsufficientSpace", manager.GetCurrent(Actor.Owner)!.Error);
        if (fault != "audit")
        { Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "audit.export")).Result); }
        else
        { Assert.Empty(TestAccounts.Audit(factory.Services, "audit.export")); }
        using var retention = factory.Services.GetRequiredService<AuditRetentionGate>().TryEnterPurge();
        Assert.NotNull(retention);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("stop")]
    public async Task GenerationCancellationRetainsOwnershipUntilIoEndsAndThenReleasesRetention(string action)
    {
        using var data = TemporaryDirectory.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        StorageSpace? Space()
        {
            if (Interlocked.Increment(ref reads) == 2)
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15), Token)) { throw new IOException("fixture gate timeout"); } }
            return Plenty;
        }
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, Space, time);
        AuditExportApiTests.Seed(factory.Services, 3);
        var manager = factory.Services.GetRequiredService<AuditExportManager>();
        var start = manager.Start(Actor, new(Action: "fixture.csv"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Token));
            Task? stop = null;
            if (action == "expire")
            { time.Advance(AuditExportManager.Lifetime); }
            else if (action == "stop")
            { stop = manager.DisposeAsync().AsTask(); Assert.False(stop.IsCompleted); }
            else
            { manager.Cancel(start.Id, Actor.Owner); }
            Assert.True(manager.GetCurrent(Actor.Owner)!.Busy);
            Assert.Equal(action == "stop" ? 503 : 409, Assert.Throws<AuditExportRequestException>(() => manager.Start(Actor, new())).StatusCode);
            Assert.Null(factory.Services.GetRequiredService<AuditRetentionGate>().TryEnterSnapshot());
            release.Set();
            if (stop is not null)
            { await stop.WaitAsync(TimeSpan.FromSeconds(10), Token); }
            await WaitAsync(() => !manager.GetCurrent(Actor.Owner)!.Busy);
            Assert.False(File.Exists(FilePath(data.Path)));
            using var lease = factory.Services.GetRequiredService<AuditRetentionGate>().TryEnterPurge();
            Assert.NotNull(lease);
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, "audit.export"));
            Assert.Equal("failure", audit.Result);
            Assert.Equal("MES audit", audit.TokenName);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SlowDownloadHoldsNoRetentionLeaseAndStopWaitsForItsCancellationAndDisposal()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path);
        AuditExportApiTests.Seed(factory.Services, 3);
        var manager = factory.Services.GetRequiredService<AuditExportManager>();
        var start = manager.Start(Actor, new());
        await WaitAsync(() => manager.GetCurrent(Actor.Owner)?.State == AuditExportState.Ready);
        using (var retention = factory.Services.GetRequiredService<AuditRetentionGate>().TryEnterPurge())
        { Assert.NotNull(retention); }
        using var lease = manager.OpenDownload(start.Id, Actor.Owner);
        using var slow = new SlowDestination();
        var copying = lease.CopyToAsync(slow, Token);
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var stop = manager.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        Assert.True(File.Exists(FilePath(data.Path)));
        lease.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(File.Exists(FilePath(data.Path)));
        Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "audit.export.download")).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyFileIsPrivateAndExpiryOrCancelDeletesOnlyItsOwnSpool(bool expire)
    {
        using var data = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, time: time);
        var manager = factory.Services.GetRequiredService<AuditExportManager>();
        var start = manager.Start(Actor, new());
        await WaitAsync(() => manager.GetCurrent(Actor.Owner)?.State == AuditExportState.Ready);
        var path = FilePath(data.Path);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        var unknown = Path.Combine(Path.GetDirectoryName(path)!, "unknown");
        await File.WriteAllTextAsync(unknown, "keep", Token);
        if (expire)
        { time.Advance(AuditExportManager.Lifetime); }
        else
        { manager.Cancel(start.Id, Actor.Owner); }
        Assert.False(File.Exists(path));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown, Token));
        Assert.Throws<AuditExportRequestException>(() => manager.OpenDownload(start.Id, Actor.Owner));
    }

    [Fact]
    public async Task CleanupFailureDisablesSubsequentExportsInsteadOfHidingAnOrphan()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path);
        var manager = factory.Services.GetRequiredService<AuditExportManager>();
        var start = manager.Start(Actor, new());
        await WaitAsync(() => manager.GetCurrent(Actor.Owner)?.State == AuditExportState.Ready);
        var path = FilePath(data.Path);
        var held = path + ".held";
        File.Move(path, held);
        Directory.CreateDirectory(path);
        try
        {
            manager.Cancel(start.Id, Actor.Owner);
            Assert.Equal("audit.exportCleanupFailed", manager.GetCurrent(Actor.Owner)!.Error);
            Assert.Equal(503, Assert.Throws<AuditExportRequestException>(() => manager.Start(Actor, new())).StatusCode);
            Assert.True(File.Exists(held));
        }
        finally { Directory.Delete(path); File.Move(held, path); }
    }

    private static FixedDataDirectoryApplicationFactory Factory(string path, Func<StorageSpace?>? space = null, TimeProvider? time = null) =>
        new(path, configureServices: services =>
        {
            services.RemoveAll<AuditExportFiles>();
            services.AddSingleton(provider => new AuditExportFiles(provider.GetRequiredService<DataDirectoryLock>(), provider.GetRequiredService<DeliveryCache>(), space));
            services.RemoveAll<AuditExportManager>();
            services.AddSingleton(provider => new AuditExportManager(provider.GetRequiredService<ConfigDatabase>(), provider.GetRequiredService<AuditRetentionGate>(),
                provider.GetRequiredService<AuditExportFiles>(), provider.GetRequiredService<AuditTrail>(), time ?? provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>()));
        });
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        { await Task.Delay(20, deadline.Token); }
    }
    private sealed class SlowDestination : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    }
}
