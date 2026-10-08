namespace TenonEdge.Runtime.Diagnostics;

/// <summary>进程共享的在途字节额度；等待不占线程，取消不会遗留额度。</summary>
public sealed class InFlightBudget(long capacity)
{
    private readonly Lock _lock = new();
    private long _used;
    private long _peak;
    private TaskCompletionSource _changed = NewSignal();
    public long Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public InFlightBudgetStatus Status
    {
        get { lock (_lock) { return new(Capacity, _used, _peak); } }
    }

    internal bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_lock)
        {
            if (bytes > Capacity - _used) { return false; }
            _used += bytes;
            _peak = Math.Max(_peak, _used);
            return true;
        }
    }

    internal async ValueTask ReserveAsync(long bytes, CancellationToken token)
    {
        if (bytes > Capacity) { throw new ArgumentOutOfRangeException(nameof(bytes), "单条消息超过在途预算。"); }
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (TryReserve(bytes)) { return; }
                changed = _changed.Task;
            }

            await changed.WaitAsync(token);
        }
    }

    internal async ValueTask WaitForAvailableAsync(long bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Capacity);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (bytes <= Capacity - _used) { return; }
                changed = _changed.Task;
            }

            await changed.WaitAsync(token);
        }
    }

    internal void Release(long bytes)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            if (bytes <= 0 || bytes > _used) { throw new InvalidOperationException("在途额度释放不匹配。"); }
            _used -= bytes;
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record InFlightBudgetStatus(long CapacityBytes, long ReservedBytes, long PeakReservedBytes);
