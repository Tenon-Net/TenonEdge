using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.SystemInfo;

namespace TenonEdge.UnitTests.Runtime;

public sealed class ClockSyncMonitorTests
{
    [Fact]
    public async Task SamplesWithoutRequestsAndStopsWithHostLifetime()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var clock = new RecordingClock();
        using var monitor = new ClockSyncMonitor(clock, time);
        await monitor.StartAsync(token);
        Assert.Equal(1, await clock.Reads.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token));

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(clock.Reads.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await clock.Reads.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, await clock.Reads.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token));

        await monitor.StopAsync(token);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(clock.Reads.Reader.TryRead(out _));
        Assert.True(monitor.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class RecordingClock : IClockSyncStatus
    {
        private int _reads;
        public Channel<int> Reads { get; } = Channel.CreateUnbounded<int>();

        public ClockSyncState State
        {
            get
            {
                Reads.Writer.TryWrite(Interlocked.Increment(ref _reads));
                return ClockSyncState.Unknown;
            }
        }
    }
}
