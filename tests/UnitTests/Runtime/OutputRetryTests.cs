using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputRetryTests
{
    [Fact]
    public void ImmediateRetrySignalsCoalesceWithinExactlyOneBackoffWindow()
    {
        var state = new OutputSenderState { RetryDelay = TimeSpan.FromSeconds(60), ConsecutiveRetries = 4 };
        Assert.False(state.RequestImmediateRetry("not-a-window"));
        state.BeginBackoff(DateTimeOffset.UtcNow.AddSeconds(60));
        var version = Assert.IsType<string>(state.Diagnostics.BackoffVersion);
        Parallel.For(0, 100, _ => Assert.True(state.RequestImmediateRetry(version)));
        Assert.True(state.ImmediateRetryRequested);
        Assert.Equal(TimeSpan.FromSeconds(60), state.RetryDelay);
        Assert.Equal(4, state.ConsecutiveRetries);
        state.EndBackoff();
        Assert.Null(state.Diagnostics.BackoffVersion);
        Assert.False(state.ImmediateRetryRequested);
        Assert.False(state.RequestImmediateRetry(version));
        state.BeginBackoff(DateTimeOffset.UtcNow.AddSeconds(60));
        Assert.NotEqual(version, state.Diagnostics.BackoffVersion);
        Assert.False(state.RequestImmediateRetry(version));
        Assert.False(state.ImmediateRetryRequested);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("manuallyPaused")]
    [InlineData("automaticallyPaused")]
    public void LeavingBackoffInvalidatesTheSignal(string next)
    {
        var state = new OutputSenderState();
        state.BeginBackoff(DateTimeOffset.UtcNow.AddSeconds(60));
        var version = state.Diagnostics.BackoffVersion!;
        Assert.True(state.RequestImmediateRetry(version));
        state.ReportState(next);
        Assert.False(state.RequestImmediateRetry(version));
        Assert.False(state.ImmediateRetryRequested);
        Assert.Null(state.Diagnostics.BackoffVersion);
    }

    [Fact]
    public void TargetRetriesDoubleWithJitterAndResetOnSuccess()
    {
        var state = new OutputSenderState();
        var backoff = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0.1, () => 0.5);
        Assert.Equal(new[] { 0.95, 1.9, 3.8, 7.6, 15.2, 30.4, 57, 57 },
            Enumerable.Range(0, 8).Select(_ => state.NextRetry(backoff, null).TotalSeconds));
        state.ConsecutiveRetries = 0;
        Assert.Equal(TimeSpan.FromSeconds(0.95), state.NextRetry(backoff, null));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(600, 60)]
    public void RetryAfterNeverShortensBackoffAndIsCapped(int requested, int expected)
    {
        var state = new OutputSenderState();
        var backoff = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0, () => 0);
        Assert.Equal(TimeSpan.FromSeconds(expected), state.NextRetry(backoff, TimeSpan.FromSeconds(requested)));
    }
}
