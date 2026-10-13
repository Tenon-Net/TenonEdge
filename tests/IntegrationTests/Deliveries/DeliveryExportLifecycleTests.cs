using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryExportLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryExportActor Actor => new("fixture-session", "admin", "127.0.0.1");

    [Theory]
    [InlineData("unknown")]
    [InlineData("bytes")]
    [InlineData("fraction")]
    [InlineData("duringWrite")]
    public async Task FreshDiskChecksPreserveQueueReserveAndDeleteAnyIncompleteFile(string failure)
    {
        using var data = TemporaryDirectory.Create();
        var reads = 0;
        StorageSpace? Space()
        {
            reads++;
            return failure switch
            {
                "unknown" => null,
                "bytes" => new(1_000_000_000, 500_000_000),
                "fraction" => new(100_000_000_000, 600_000_000),
                _ => reads == 1 ? new(10_000_000_000, 5_000_000_000) : new(10_000_000_000, 500_000_000),
            };
        }
        await using var factory = Factory(data.Path, Space);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await DeliveryExportApiTests.SeedAsync(cache, DeliveryState.Failed);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Failed));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        await WaitAsync(manager, DeliveryExportState.Failed);
        await DeliveryExportApiTests.WaitUntilAsync(() => TestAccounts.Audit(factory.Services, "delivery.export").Count == 1);
        Assert.Equal("delivery.exportInsufficientSpace", manager.GetCurrent(Actor.Owner)!.Error);
        Assert.False(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
        Assert.Single(cache.Inspect(new(DeliveryState.Failed)).Items);
        Assert.Equal(failure == "duringWrite" ? 2 : 1, reads);
        Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export")).Result);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("stop")]
    public async Task GenerationOwnsSlotAndStopWaitsUntilCancelledIoAndAuditFinish(string action)
    {
        using var data = TemporaryDirectory.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        StorageSpace? Space()
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15), Token))
                { throw new IOException("fixture gate timeout"); }
            }
            return new(10_000_000_000, 5_000_000_000);
        }
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, Space, time);
        await DeliveryExportApiTests.SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Failed);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Failed));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Token));
            Assert.True(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
            Task? stop = null;
            if (action == "cancel")
            { manager.Cancel(prepared.Id, Actor.Owner); }
            else if (action == "expire")
            { time.Advance(DeliveryExportManager.Lifetime); }
            else
            { stop = manager.DisposeAsync().AsTask(); Assert.False(stop.IsCompleted); }
            var expected = action == "expire" ? DeliveryExportState.Expired : DeliveryExportState.Cancelled;
            Assert.Equal(expected, manager.GetCurrent(Actor.Owner)!.State);
            var denied = Assert.Throws<DeliveryExportRequestException>(() => manager.Prepare(Actor, new(DeliveryState.Failed)));
            Assert.Equal(action == "stop" ? 503 : 409, denied.StatusCode);
            release.Set();
            if (stop is not null)
            { await stop.WaitAsync(TimeSpan.FromSeconds(10), Token); }
            await DeliveryExportApiTests.WaitUntilAsync(() => TestAccounts.Audit(factory.Services, "delivery.export").Count == 1);
            Assert.False(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
            Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export")).Result);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyFileIsDeletedOnCancelOrExpiryAndDoesNotDeleteUnknownFiles(bool expire)
    {
        using var data = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, time: time);
        await DeliveryExportApiTests.SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Quarantined);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Quarantined));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        await WaitAsync(manager, DeliveryExportState.Ready);
        var unknown = Path.Combine(data.Path, DeliveryExportFiles.DirectoryName, "unknown.txt");
        await File.WriteAllTextAsync(unknown, "keep", Token);
        if (expire)
        { time.Advance(DeliveryExportManager.Lifetime); }
        else
        { manager.Cancel(prepared.Id, Actor.Owner); }
        Assert.False(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown, Token));
        Assert.Throws<DeliveryExportRequestException>(() => manager.OpenDownload(prepared.Id, Actor.Owner));
        Assert.Equal(expire ? DeliveryExportState.Expired : DeliveryExportState.Cancelled, manager.GetCurrent(Actor.Owner)!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowDownloadIsCancelledAndStopWaitsForLeaseRelease(bool expire)
    {
        using var data = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Factory(data.Path, time: time);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await DeliveryExportApiTests.SeedAsync(cache, DeliveryState.Failed);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Failed));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        await WaitAsync(manager, DeliveryExportState.Ready);
        using var lease = manager.OpenDownload(prepared.Id, Actor.Owner);
        using var destination = new SlowDestination();
        var copying = lease.CopyToAsync(destination, Token);
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Throws<DeliveryExportRequestException>(() => manager.OpenDownload(prepared.Id, Actor.Owner));
        Assert.True((await cache.EnqueueAsync([new(Guid.NewGuid(), 11, Guid.NewGuid(), "meter", 1, "new"u8.ToArray(), "fp", "owner")], Token)).Committed);
        if (expire)
        { time.Advance(DeliveryExportManager.Lifetime); }
        var stop = manager.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        Assert.True(File.Exists(DeliveryExportApiTests.FilePath(data.Path))); // Handle is still owned until lease disposal.
        lease.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
        Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export.download")).Result);
    }

    [Fact]
    public async Task DownloadIoFailureIsNotReportedAsWrittenAndDeletesFile()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path);
        await DeliveryExportApiTests.SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Failed);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Failed));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        await WaitAsync(manager, DeliveryExportState.Ready);
        using (var lease = manager.OpenDownload(prepared.Id, Actor.Owner))
        using (var destination = new MemoryStream([], writable: false))
        { await Assert.ThrowsAsync<NotSupportedException>(() => lease.CopyToAsync(destination, Token)); }
        Assert.Equal(DeliveryExportState.Failed, manager.GetCurrent(Actor.Owner)!.State);
        Assert.Equal("delivery.exportDownloadFailed", manager.GetCurrent(Actor.Owner)!.Error);
        Assert.False(File.Exists(DeliveryExportApiTests.FilePath(data.Path)));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    public async Task StartupRefusesLinkedExportPathsWithoutFollowingOrDeletingTheirTargets(string link)
    {
        if (OperatingSystem.IsWindows())
        { return; } // Supported production boundary is Linux; Windows ACL/link privileges not claimed.
        using var data = TemporaryDirectory.Create();
        using var external = TemporaryDirectory.Create();
        var privateTarget = Path.Combine(external.Path, DeliveryExportFiles.FileName);
        await File.WriteAllTextAsync(privateTarget, "do-not-touch", Token);
        var directory = Path.Combine(data.Path, DeliveryExportFiles.DirectoryName);
        if (link == "directory")
        { Directory.CreateSymbolicLink(directory, external.Path); }
        else
        {
            Directory.CreateDirectory(directory);
            File.CreateSymbolicLink(DeliveryExportApiTests.FilePath(data.Path), privateTarget);
        }
        await using var factory = Factory(data.Path);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        Assert.Equal(503, Assert.Throws<DeliveryExportRequestException>(() => manager.Prepare(Actor, new(DeliveryState.Failed))).StatusCode);
        Assert.Equal("do-not-touch", await File.ReadAllTextAsync(privateTarget, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureIsVisibleAndDisablesFurtherExportsUntilRestart(bool downloaded)
    {
        if (!OperatingSystem.IsLinux())
        { return; }
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path);
        await DeliveryExportApiTests.SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Failed);
        var manager = factory.Services.GetRequiredService<DeliveryExportManager>();
        var prepared = manager.Prepare(Actor, new(DeliveryState.Failed));
        await WaitAsync(manager, DeliveryExportState.AwaitingConfirmation);
        manager.Confirm(prepared.Id, Actor.Owner);
        await WaitAsync(manager, DeliveryExportState.Ready);
        var path = DeliveryExportApiTests.FilePath(data.Path);
        // Linux permits moving an open file. A directory at the reserved path makes unlink fail even under root.
        // This is a test-side filesystem fault, not a product file-move operation or physical disk-full test.
        var held = path + ".fixture-held";
        File.Move(path, held);
        Directory.CreateDirectory(path);
        try
        {
            if (downloaded)
            {
                using var lease = manager.OpenDownload(prepared.Id, Actor.Owner);
                using var destination = new MemoryStream();
                await lease.CopyToAsync(destination, Token);
            }
            else
            { manager.Cancel(prepared.Id, Actor.Owner); }
            var status = manager.GetCurrent(Actor.Owner)!;
            Assert.Equal(downloaded ? DeliveryExportState.ResponseWritten : DeliveryExportState.Cancelled, status.State);
            Assert.Equal("delivery.exportCleanupFailed", status.Error);
            Assert.Equal(503, Assert.Throws<DeliveryExportRequestException>(() => manager.Prepare(Actor, new(DeliveryState.Failed))).StatusCode);
            Assert.True(File.Exists(held));
        }
        finally { Directory.Delete(path); File.Move(held, path); }
    }

    private static FixedDataDirectoryApplicationFactory Factory(string path, Func<StorageSpace?>? space = null, TimeProvider? time = null) =>
        new(path, configureServices: services =>
        {
            services.RemoveAll<DeliveryExportFiles>();
            services.AddSingleton(provider => new DeliveryExportFiles(provider.GetRequiredService<DataDirectoryLock>(),
                provider.GetRequiredService<DeliveryCache>(), space));
            services.RemoveAll<DeliveryExportManager>();
            services.AddSingleton(provider => new DeliveryExportManager(provider.GetRequiredService<DeliveryCache>(),
                provider.GetRequiredService<DeliveryExportFiles>(), provider.GetRequiredService<AuditTrail>(),
                time ?? provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>()));
        });

    private static Task WaitAsync(DeliveryExportManager manager, DeliveryExportState state) =>
        DeliveryExportApiTests.WaitUntilAsync(() => manager.GetCurrent(Actor.Owner)?.State == state);

    private sealed class SlowDestination : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
