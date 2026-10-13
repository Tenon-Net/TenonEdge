using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputSenderDiagnosticsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ErrorAndPauseDescriptionsAreBounded()
    {
        var state = new OutputSenderState();
        var error = new string('e', 10000);
        state.ReportResult(new(OutputSendStatus.SuspendTarget, error), At);
        state.ReportState("automaticallyPaused", pauseReason: error);
        Assert.Equal(500, state.Diagnostics.LastError!.Length);
        Assert.Equal(500, state.Diagnostics.PauseReason!.Length);
    }

    [Fact]
    public void PublishedSnapshotsAreImmutableAndKeepFirstBlockedTimeUntilHeadChanges()
    {
        var state = new OutputSenderState();
        var id = TenonEdge.Abstractions.Plans.RuntimeUnitId.Create("output.http", "old-route/out");
        state.ReportUnit(id);
        state.ReportHead(42);
        state.ReportResult(new(OutputSendStatus.Retry, "timeout"), At);
        state.ReportState("backoff", At.AddSeconds(10));
        var first = state.Diagnostics;
        Assert.Equal(id, first.UnitId);
        Assert.Equal(42, first.HeadSequence);
        Assert.Equal(At, first.HeadBlockedSince);
        Assert.Equal(At.AddSeconds(10), first.RetryAt);
        state.ReportResult(new(OutputSendStatus.Retry, "still unavailable"), At.AddSeconds(10));
        Assert.Equal(At, state.Diagnostics.HeadBlockedSince);
        Assert.Equal("timeout", first.LastError);
        state.ReportState("automaticallyPaused", pauseReason: "credentials rejected");
        Assert.Null(state.Diagnostics.RetryAt);
        Assert.Equal("credentials rejected", state.Diagnostics.PauseReason);
        state.ReportState("normal");
        Assert.Null(state.Diagnostics.PauseReason);
        state.ReportHead(43);
        Assert.Null(state.Diagnostics.HeadBlockedSince);
        Assert.Equal(At.AddSeconds(10), state.Diagnostics.LastResultAt);
        state.ReportResult(new(OutputSendStatus.Ack), At.AddSeconds(20));
        Assert.Null(state.Diagnostics.LastError);
        state.ReportHead(null);
        Assert.Null(state.Diagnostics.HeadSequence);
    }
}
