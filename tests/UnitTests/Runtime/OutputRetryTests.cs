using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputRetryTests
{
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
