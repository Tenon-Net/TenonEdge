using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.SystemInfo;

namespace TenonEdge.UnitTests.Runtime;

public sealed class ClockSyncTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1, ClockSyncState.Unknown)]
    [InlineData(0, ClockSyncState.Synchronized)]
    [InlineData(1, ClockSyncState.Synchronized)]
    [InlineData(2, ClockSyncState.Synchronized)]
    [InlineData(3, ClockSyncState.Synchronized)]
    [InlineData(4, ClockSyncState.Synchronized)]
    [InlineData(5, ClockSyncState.NotSynchronized)]
    [InlineData(6, ClockSyncState.Unknown)]
    public void KernelResultsDoNotTreatLeapSecondsAsUnsynchronized(int result, ClockSyncState expected) =>
        Assert.Equal(expected, ClockSyncStatus.Classify(result));

    [Fact]
    public void MonotonicCacheRefreshesAndOnlyRecordsTransitions()
    {
        var time = new FakeTimeProvider();
        var events = new Events();
        var calls = 0;
        var result = 5;
        var clock = new ClockSyncStatus(time, events, () => { calls++; return result; });
        Assert.Equal(ClockSyncState.NotSynchronized, clock.State);
        result = 0;
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(ClockSyncState.NotSynchronized, clock.State);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ClockSyncState.Synchronized, clock.State);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ClockSyncState.Synchronized, clock.State);
        Assert.Equal(3, calls);
        Assert.Equal(2, events.Count);
        result = -1;
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ClockSyncState.Unknown, clock.State);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void NativeReadIsSafeWithoutClockModificationPrivileges()
    {
        var result = ClockSyncStatus.ReadKernel();
        output.WriteLine($"OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; "
            + $"Architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; "
            + $"adjtimex={result}; State={ClockSyncStatus.Classify(result)}");
        Assert.InRange(result, -1, 5);
    }

    private sealed class Events : IRuntimeEventSink
    {
        public int Count { get; private set; }
        public void Record(RuntimeEventSeverity severity, string type, string? target, string description) => Count++;
    }
}
