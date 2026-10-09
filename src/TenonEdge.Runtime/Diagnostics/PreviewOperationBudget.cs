namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 真实预览与测试共用的即时准入额度（设计 §8.7），不排队、不保存样本。
/// 会话标识须由宿主从已认证会话取得，不能使用请求提供的分区标识。
/// 租约由实际操作持有至 I/O 和资源清理结束；请求取消本身不释放额度。
/// </summary>
public sealed class PreviewOperationBudget
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private readonly int _globalLimit;
    private readonly int _sessionLimit;
    private int _active;

    public PreviewOperationBudget(int globalLimit = 4, int sessionLimit = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sessionLimit, globalLimit);
        _globalLimit = globalLimit;
        _sessionLimit = sessionLimit;
    }

    public IDisposable? TryAcquire(string authenticatedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedSessionId);
        lock (_lock)
        {
            _sessions.TryGetValue(authenticatedSessionId, out var count);
            if (_active >= _globalLimit || count >= _sessionLimit)
            { return null; }
            var lease = new Lease(this, authenticatedSessionId);
            _sessions[authenticatedSessionId] = count + 1;
            _active++;
            return lease;
        }
    }

    private void Release(string sessionId)
    {
        lock (_lock)
        {
            var remaining = _sessions[sessionId] - 1;
            if (remaining == 0)
            { _sessions.Remove(sessionId); }
            else
            { _sessions[sessionId] = remaining; }
            _active--;
        }
    }

    private sealed class Lease(PreviewOperationBudget owner, string sessionId) : IDisposable
    {
        private PreviewOperationBudget? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(sessionId);
    }
}
