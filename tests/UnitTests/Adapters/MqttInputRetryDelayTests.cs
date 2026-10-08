using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttInputRetryDelayTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(0.999999)]
    public void FailuresGrowExponentiallyAndRemainBounded(double sample)
    {
        var retry = new MqttInputRetryDelay(() => sample);
        double[] seconds = [1, 2, 4, 8, 16, 30, 30];
        foreach (var value in seconds)
        {
            var delay = retry.Next(connected: false);
            Assert.Equal(TimeSpan.FromSeconds(value * (1 - (0.1 * sample))), delay);
            Assert.InRange(delay.TotalSeconds, value * 0.9, value);
        }
        for (var i = 0; i < 10000; i++)
        { Assert.InRange(retry.Next(connected: false).TotalSeconds, 27, 30); }
    }

    [Fact]
    public void SuccessResetsDelayWithoutConsumingRandomness()
    {
        var samples = 0;
        var retry = new MqttInputRetryDelay(() => { samples++; return 0.5; });
        for (var i = 0; i < 10; i++)
        { _ = retry.Next(connected: false); }
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Next(connected: true));
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Next(connected: true));
        Assert.Equal(10, samples);
        Assert.Equal(TimeSpan.FromSeconds(0.95), retry.Next(connected: false));
        Assert.Equal(11, samples);
    }

    [Fact]
    public void IndependentLoopsDoNotShareFailureState()
    {
        var first = new MqttInputRetryDelay(() => 0);
        var second = new MqttInputRetryDelay(() => 0.5);
        _ = first.Next(connected: false);
        Assert.Equal(TimeSpan.FromSeconds(2), first.Next(connected: false));
        Assert.Equal(TimeSpan.FromSeconds(0.95), second.Next(connected: false));
    }
}
