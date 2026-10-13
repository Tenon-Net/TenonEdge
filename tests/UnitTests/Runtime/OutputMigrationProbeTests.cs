using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputMigrationProbeTests
{
    [Fact]
    public void MigrationOnlySignalsStalePoisonProbeResetAndOwnerConsumesItWithoutClearingOtherState()
    {
        var state = new OutputSenderState
        {
            RecheckHeadSeq = 12,
            AcknowledgedSeq = 99,
            ConsecutiveRejects = 4,
            ConsecutiveRetries = 3,
            RetryDelay = TimeSpan.FromSeconds(10),
            SuspendedVersion = "v1",
        };
        state.RequestQueueProbeReset();
        Assert.Equal(12, state.RecheckHeadSeq); // No request-thread mutation of loop-owned fields.
        state.ApplyQueueProbeReset();
        Assert.Null(state.RecheckHeadSeq);
        Assert.Equal(99, state.AcknowledgedSeq);
        Assert.Equal(4, state.ConsecutiveRejects);
        Assert.Equal(3, state.ConsecutiveRetries);
        Assert.Equal(TimeSpan.FromSeconds(10), state.RetryDelay);
        Assert.Equal("v1", state.SuspendedVersion);
        state.RecheckHeadSeq = 13;
        state.ApplyQueueProbeReset();
        Assert.Equal(13, state.RecheckHeadSeq); // The signal is consumed once, not on each loop.
    }
}
