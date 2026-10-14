namespace TenonEdge.Host.Storage;

/// <summary>审计导出、保留参数变更与过期删除互斥；此门闩不持有数据库连接/事务，不阻塞普通审计写入或运行事件清理。</summary>
internal sealed class AuditRetentionGate
{
    private readonly Lock _gate = new();
    private bool _occupied;
    private bool _purgePending;
    public bool PurgePending { get { lock (_gate) { return _purgePending; } } }

    // A policy write shares the gate so no old-cutoff purge can continue after
    // a new retention period is committed and published.
    public IDisposable? TryEnterPolicyChange() => TryEnterSnapshot();

    public IDisposable? TryEnterSnapshot()
    {
        lock (_gate)
        {
            if (_occupied || _purgePending)
            { return null; }
            _occupied = true;
            return new Lease(this);
        }
    }

    public IDisposable? TryEnterPurge()
    {
        lock (_gate)
        {
            // 已延后的保留清理优先，连续导出不能饿死清理。
            if (_occupied)
            { _purgePending = true; return null; }
            _purgePending = false;
            _occupied = true;
            return new Lease(this);
        }
    }
    private sealed class Lease(AuditRetentionGate owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { lock (owner._gate) { owner._occupied = false; } }
        }
    }
}
