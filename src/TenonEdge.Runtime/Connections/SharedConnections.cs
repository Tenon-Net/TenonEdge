using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Connections;

/// <summary>一个逻辑连接一个会话；引用计数、串行访问及最后释放集中在运行时，不由协议适配器各建一套。</summary>
public sealed class SharedConnections : ISharedConnections
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, Entry> _entries = [];
    public const int MaximumConnections = 500;

    public SharedConnectionObservation? Observe(long connectionId, string protocol, string configurationIdentity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        lock (_lock)
        {
            if (!_entries.TryGetValue(connectionId, out var entry) || entry.Protocol != protocol || entry.Identity != configurationIdentity)
            { return null; }
            return new(entry.Closing, (entry.Session as IConnectionObservationSource)?.Observation);
        }
    }

    public IConnectionLease<T> Acquire<T>(long connectionId, string protocol, string configurationIdentity, Func<T> create)
        where T : class, IAsyncDisposable
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationIdentity);
        ArgumentNullException.ThrowIfNull(create);
        var key = connectionId;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var current))
            {
                if (current.Closing || current.Protocol != protocol || current.Identity != configurationIdentity || current.Session is not T)
                {
                    throw new InvalidOperationException("连接正在释放，或同一连接的协议配置不一致。");
                }
                current.References++;
                return new Lease<T>(this, key, current);
            }
            if (_entries.Count >= MaximumConnections)
            { throw new InvalidOperationException("共享连接达到 500 个上限。"); }
            var session = create() ?? throw new InvalidOperationException("连接工厂返回空实例。");
            var entry = new Entry(session, protocol, configurationIdentity);
            _entries.Add(key, entry);
            return new Lease<T>(this, key, entry);
        }
    }

    public IConnectionLease<T> AcquireDiagnostic<T>(string protocol, Func<T> create) where T : class, IAsyncDisposable
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(create);
        lock (_lock)
        {
            if (_entries.Count >= MaximumConnections)
            { throw new InvalidOperationException("共享连接达到 500 个上限。"); }
            // 业务 ID 为正；有限连接集合中的负数空槽只属于本次诊断，不由调用者指定。
            long key = -1;
            while (_entries.ContainsKey(key))
            { key--; }
            var session = create() ?? throw new InvalidOperationException("连接工厂返回空实例。");
            var entry = new Entry(session, protocol, "diagnostic");
            _entries.Add(key, entry);
            return new Lease<T>(this, key, entry);
        }
    }

    private async ValueTask ReleaseAsync(long key, Entry entry)
    {
        lock (_lock)
        {
            if (--entry.References != 0)
            { return; }
            entry.Closing = true;
        }
        // 等实际 I/O 退出再释放。即使取消不合作，也保留标识，不能建立第二实例。
        await entry.Serial.WaitAsync();
        try
        {
            await entry.Session.DisposeAsync();
            lock (_lock)
            { _entries.Remove(key); }
        }
        finally { entry.Serial.Release(); }
        // 不释放 SemaphoreSlim：已经排队但租约被释放的调用仍须醒来并得到 ObjectDisposedException。
    }

    private sealed class Entry(IAsyncDisposable session, string protocol, string identity)
    {
        public IAsyncDisposable Session { get; } = session;
        public string Identity { get; } = identity;
        public string Protocol { get; } = protocol;
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public int References { get; set; } = 1;
        public bool Closing { get; set; }
    }

    private sealed class Lease<T>(SharedConnections owner, long key, Entry entry) : IConnectionLease<T>
        where T : class, IAsyncDisposable
    {
        private int _disposed;
        private readonly Lock _disposeLock = new();
        private Task? _disposeTask;

        public async Task<TResult> ExecuteAsync<TResult>(Func<T, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await entry.Serial.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return await operation((T)entry.Session, cancellationToken);
            }
            finally { entry.Serial.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                if (_disposeTask is null)
                {
                    Volatile.Write(ref _disposed, 1);
                    _disposeTask = owner.ReleaseAsync(key, entry).AsTask();
                }
                return new ValueTask(_disposeTask);
            }
        }
    }
}
