using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class PendingCounterTests
{
    [Fact]
    public void CommitOnlyRemovesTheCapturedIncrement()
    {
        var counters = new PendingCounters();
        counters.Add("input/meter/read/overruns", 3);
        var snapshot = counters.Snapshot();
        counters.Add("input/meter/read/overruns", 5);
        counters.Add("storageFailedBatches");
        Assert.Equal(3, snapshot["input/meter/read/overruns"]);
        counters.Committed(snapshot);
        Assert.Equal(5, counters.Snapshot()["input/meter/read/overruns"]);
        Assert.Equal(1, counters.Snapshot()["storageFailedBatches"]);
        counters.Committed(counters.Snapshot());
        Assert.False(counters.HasValues);
    }

    [Fact]
    public async Task ConcurrentIncrementsAreNotLost()
    {
        var counters = new PendingCounters();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            { counters.Add("input/meter/read/overruns"); }
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(2000, counters.Snapshot()["input/meter/read/overruns"]);
    }
}
