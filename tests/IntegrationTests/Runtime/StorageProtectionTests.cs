using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class StorageProtectionTests
{
    [Theory]
    [InlineData(1000, 499, 500, 0, true)]
    [InlineData(10000, 499, 0, 0.05, true)]
    [InlineData(10000, 500, 500, 0.05, false)]
    [InlineData(0, 0, 0, 0, true)]
    public void ByteAndPercentageThresholdsAreIndependent(long total, long available, long bytes, double fraction, bool blocked)
    {
        var protection = new StorageProtection("/", bytes, fraction, () => new(total, available));
        Assert.Equal(blocked, protection.Refresh().RejectNewRecords);
    }

    [Fact]
    public async Task ProtectionRejectsNewRecordsButAllowsAckDeletionAndRecoversWithPersistentCounts()
    {
        using var directory = TemporaryDirectory.Create();
        StorageSpace? space = new(10000, 10000);
        var cache = new DeliveryCache(new DeliveryCacheOptions
        {
            DatabasePath = Path.Combine(directory.Path, DeliveryCacheOptions.FileName),
            ConfigStoreId = RuntimeTesting.ConfigStoreId,
            MinimumDiskFreeBytes = 500,
            CommitInterval = TimeSpan.FromMilliseconds(1),
        }, TimeProvider.System, () => space);
        var events = new RecordingEventSink();
        await using var writer = new DeliveryWriterUnit(cache, TimeProvider.System, events, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var running = writer.RunAsync(stop.Token);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var first = await cache.EnqueueAsync([Draft()], token);
            var seq = Assert.Single(first.Seqs);
            space = new(10000, 499);
            var refused = await cache.EnqueueAsync([Draft(), Draft()], token);
            Assert.True(refused.Committed);
            Assert.Empty(refused.Seqs);
            Assert.Equal(2, refused.StorageProtectionRejectedRecords);
            Assert.Equal(0, refused.QuotaRejectedRecords);
            Assert.Equal(1, (await cache.DeleteAsync([seq], token)).AffectedRecords);
            Assert.Empty(cache.ReadPending(1, 0, 10));
            space = null;
            Assert.Equal(1, (await cache.EnqueueAsync([Draft()], token)).StorageProtectionRejectedRecords);
            Assert.True(cache.StorageProtection.Status.SpaceUnknown);
            space = new(10000, 9000);
            Assert.Single((await cache.EnqueueAsync([Draft()], token)).Seqs);
            Assert.False(cache.StorageProtection.Status.RejectNewRecords);
            Assert.Equal(3, events.OfType("storage.protection").Count);
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10), token);
            cache.Store.ClearReadPool();
        }

        var reopened = RuntimeTesting.CreateDeliveryCache(directory.Path);
        var counters = reopened.GetPersistentCounters();
        Assert.Equal(3, counters["storageProtectionRejectedRecords"]);
        Assert.Equal(2, counters["committedRecords"]);
        Assert.Equal(1, counters["acknowledgedRecords"]);
        reopened.Store.ClearReadPool();
    }

    private static DeliveryDraft Draft() => new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1, "{}"u8.ToArray(), "fp", "target");
}
