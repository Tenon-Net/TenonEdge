namespace TenonEdge.Runtime.Deliveries;

/// <summary>连接身份变更与报文冻结到接管结果之间的屏障。只保存计数，不持有数据库、报文或网络资源。
/// 一批目标原子预约，避免扇出多目标相互等待；等待变更的连接不再接受新预约，其他连接不受影响。</summary>
public sealed class DeliveryIdentityBarrier
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, int> _publications = [];
    private readonly HashSet<long> _changes = [];
    private TaskCompletionSource _changed = Signal();

    public async ValueTask<IDisposable> EnterPublicationAsync(IEnumerable<long> connectionIds, CancellationToken token)
    {
        var ids = connectionIds.Distinct().ToArray();
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (!ids.Any(_changes.Contains))
                {
                    foreach (var id in ids)
                    { _publications[id] = _publications.GetValueOrDefault(id) + 1; }
                    return new Lease(() => ReleasePublication(ids));
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>调用方在数据库事务外有界等待，并持有租约跨越队列快照检查及凭证 COMMIT。
    /// 取消只撤销尚未取得的变更租约，不撤销已被写者接管的发布。</summary>
    public async ValueTask<IDisposable> EnterChangeAsync(long connectionId, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (_changes.Add(connectionId))
                { break; }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_publications.ContainsKey(connectionId))
                    { return new Lease(() => ReleaseChange(connectionId)); }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            ReleaseChange(connectionId);
            throw;
        }
    }

    private void ReleasePublication(long[] ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (--_publications[id] == 0)
                { _publications.Remove(id); }
            }
            if (_changes.Count > 0)
            { Pulse(); }
        }
    }

    private void ReleaseChange(long id)
    {
        lock (_lock)
        {
            _changes.Remove(id);
            Pulse();
        }
    }

    private void Pulse()
    {
        var previous = _changed;
        _changed = Signal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
