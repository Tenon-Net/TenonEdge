namespace TenonEdge.Adapters.Mqtt;

/// <summary>输入连接维护：成功后恢复一秒探测，失败指数退避并随机缩短最多 10%，上限三十秒。</summary>
internal sealed class MqttInputRetryDelay(Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;
    private double _seconds = 1;

    public TimeSpan Next(bool connected)
    {
        if (connected)
        {
            _seconds = 1;
            return TimeSpan.FromSeconds(1);
        }

        var delay = TimeSpan.FromSeconds(_seconds * (1 - (0.1 * _random())));
        _seconds = Math.Min(30, _seconds * 2);
        return delay;
    }
}
