using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ConnectionObservationTrackerTests
{
    [Fact]
    public void SuccessAndDisconnectPreserveLastErrorWithoutMutatingPreviouslyReadSnapshots()
    {
        var time = new FakeTimeProvider();
        var tracker = new ConnectionObservationTracker(time);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), tracker.Snapshot);
        tracker.Failed("http.timeout");
        var failed = tracker.Snapshot;
        Assert.Equal(time.GetUtcNow(), failed.LastErrorAt);
        time.Advance(TimeSpan.FromSeconds(2));
        tracker.Succeeded();
        var success = tracker.Snapshot;
        Assert.Equal(ConnectionCommunicationState.Connected, success.State);
        Assert.Equal(time.GetUtcNow(), success.LastSuccessAt);
        Assert.Equal(failed.LastErrorAt, success.LastErrorAt);
        Assert.Equal("http.timeout", success.LastErrorCode);
        tracker.Disconnected();
        Assert.Equal(success with { State = ConnectionCommunicationState.Connecting }, tracker.Snapshot);
        Assert.Equal(ConnectionCommunicationState.Degraded, failed.State);
        Assert.Null(failed.LastSuccessAt);
    }
}
