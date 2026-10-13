using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Infrastructure;

internal sealed class MigrationPollClock : TimeProvider
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private int _pollTimers;
    private int _polls;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        if (dueTime == RuntimeCoordinator.MigrationPollInterval)
        { Interlocked.Increment(ref _pollTimers); }
        return timer;
    }
    public async Task FireAsync()
    {
        await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref _pollTimers) > _polls);
        _clock.Advance(RuntimeCoordinator.MigrationPollInterval);
        _polls++;
    }
    public async Task PollAsync()
    {
        await FireAsync();
        await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref _pollTimers) > _polls);
    }
}
