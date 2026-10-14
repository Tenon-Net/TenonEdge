using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>进程共享的在途字节额度；可在总额度内划出原始输入分区，剩余供解析/分发。
/// 分区不互借，避免已接收的原始报文挤占其解析所需额度；状态和峰值统计包含两者。
/// 普通等待不占线程；原始缓冲租约必须由数据实际持有者释放，不随等待取消释放。</summary>
public sealed class InFlightBudget : IInputBufferBudget
{
    private readonly Lock _lock = new();
    private long _capacity;
    private long _used;
    private long _peak;
    private long _bufferUsed;
    private TaskCompletionSource _changed = NewSignal();
    private TaskCompletionSource _bufferChanged = NewSignal();
    public InFlightBudget(long capacity, long bufferCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bufferCapacity, capacity);
        _capacity = capacity;
        BufferCapacity = bufferCapacity;
    }

    public long Capacity { get { lock (_lock) { return _capacity; } } }
    public long BufferCapacity { get; }
    internal long WorkingCapacity => Capacity - BufferCapacity;

    /// <summary>不撤销已持有的租约。降低后新准入按新值，超额部分由原所有者释放；
    /// 唤醒等待者以重检大小，升高无需等待旧消息释放。原始缓冲分区大小不变。</summary>
    public void ConfigureCapacity(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, BufferCapacity);
        TaskCompletionSource changed;
        TaskCompletionSource bufferChanged;
        lock (_lock)
        {
            if (_capacity == capacity)
            { return; }
            changed = _changed;
            bufferChanged = _bufferChanged;
            _changed = NewSignal();
            _bufferChanged = NewSignal();
            _capacity = capacity;
        }
        changed.TrySetResult();
        bufferChanged.TrySetResult();
    }

    public IDisposable? TryReserveBuffer(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_lock)
        {
            if (bytes > BufferCapacity - _bufferUsed || bytes > _capacity - _used - _bufferUsed)
            { return null; }
            var lease = new BufferLease(this, bytes);
            _bufferUsed += bytes;
            _peak = Math.Max(_peak, _used + _bufferUsed);
            return lease;
        }
    }

    public async ValueTask<IDisposable> ReserveBufferAsync(long bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, BufferCapacity);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryReserveBuffer(bytes) is { } lease)
                { return lease; }
                changed = _bufferChanged.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    private void ReleaseBuffer(long bytes)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _bufferUsed -= bytes;
            changed = _bufferChanged;
            _bufferChanged = NewSignal();
        }
        changed.TrySetResult();
    }

    private sealed class BufferLease(InFlightBudget owner, long bytes) : IDisposable
    {
        private InFlightBudget? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseBuffer(bytes);
    }

    public InFlightBudgetStatus Status
    {
        get { lock (_lock) { return new(Capacity, _used + _bufferUsed, _peak); } }
    }

    internal bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_lock)
        {
            if (bytes > WorkingCapacity - _used)
            { return false; }
            _used += bytes;
            _peak = Math.Max(_peak, _used + _bufferUsed);
            return true;
        }
    }

    internal async ValueTask ReserveAsync(long bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (bytes > WorkingCapacity)
        { throw new ArgumentOutOfRangeException(nameof(bytes), "单条消息超过在途预算。"); }
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, WorkingCapacity);
                if (TryReserve(bytes))
                { return; }
                changed = _changed.Task;
            }

            await changed.WaitAsync(token);
        }
    }

    internal async ValueTask WaitForAvailableAsync(long bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, WorkingCapacity);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, WorkingCapacity);
                if (bytes <= WorkingCapacity - _used)
                { return; }
                changed = _changed.Task;
            }

            await changed.WaitAsync(token);
        }
    }

    internal void Release(long bytes)
    {
        TaskCompletionSource changed;
        TaskCompletionSource bufferChanged;
        lock (_lock)
        {
            if (bytes <= 0 || bytes > _used)
            { throw new InvalidOperationException("在途额度释放不匹配。"); }
            _used -= bytes;
            changed = _changed;
            bufferChanged = _bufferChanged;
            _changed = NewSignal();
            _bufferChanged = NewSignal();
        }

        changed.TrySetResult();
        bufferChanged.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record InFlightBudgetStatus(long CapacityBytes, long ReservedBytes, long PeakReservedBytes);
