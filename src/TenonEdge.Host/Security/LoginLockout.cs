namespace TenonEdge.Host.Security;

/// <summary>
/// 登录失败锁定（总设计 §3.3、§8.4）：同一账号名连续 5 次失败锁定 15 分钟；不存在的账号名同样计数，提示与已有账号一致。
/// <list type="bullet">
/// <item>同一账号名的登录与修改密码尝试经 <see cref="EnterAsync"/> 逐个处理：前一次尝试的计数与锁定提交之后，下一次才开始检查，
/// 并发请求不能在锁定生效之前多试密码，锁定期间的尝试也不会延长锁定。只有一个进程写配置库（数据目录锁），进程内排队即可。</item>
/// <item>已有账号的计数与锁定保存在账号记录中（重启后仍然有效）；不存在的账号名只在内存中计数，最多
/// <see cref="MaximumTrackedUnknownNames"/> 条：超出时先移除已过期的，仍然超出时移除最早一次失败的，避免任意账号名耗尽内存或写满配置库。</item>
/// </list>
/// </summary>
internal sealed class LoginLockout(TimeProvider time)
{
    public const int MaximumFailures = 5;

    public const int MaximumTrackedUnknownNames = 10_000;

    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    // 按账号名的哈希分到固定数量的队列上；不同账号名偶尔共用一个队列，只是多排一会儿队。
    private const int GateCount = 64;

    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, GateCount).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly Lock _unknownNamesLock = new();
    private readonly Dictionary<string, UnknownNameState> _unknownNames = new(StringComparer.Ordinal);

    /// <summary>当前在内存中计数的不存在的账号名数量。</summary>
    public int TrackedUnknownNames
    {
        get
        {
            lock (_unknownNamesLock)
            {
                return _unknownNames.Count;
            }
        }
    }

    /// <summary>等待轮到给定账号名（规范化后）的这次尝试；释放返回的对象后，同一账号名的下一次尝试才能开始。</summary>
    public async Task<IDisposable> EnterAsync(string normalizedName, CancellationToken cancellationToken)
    {
        var gate = _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(normalizedName) % GateCount)];
        await gate.WaitAsync(cancellationToken);
        return new GateRelease(gate);
    }

    /// <summary>不存在的账号名当前被锁定到的时间；没有锁定时返回 null。</summary>
    public DateTimeOffset? UnknownNameLockedUntil(string normalizedName)
    {
        var now = time.GetUtcNow();
        lock (_unknownNamesLock)
        {
            return _unknownNames.TryGetValue(normalizedName, out var state) && state.LockedUntil > now ? state.LockedUntil : null;
        }
    }

    /// <summary>记录不存在的账号名的一次失败；达到上限时返回锁定到的时间。</summary>
    public DateTimeOffset? RecordUnknownNameFailure(string normalizedName)
    {
        var now = time.GetUtcNow();
        lock (_unknownNamesLock)
        {
            if (!_unknownNames.TryGetValue(normalizedName, out var state))
            {
                MakeRoomForOneMore(now);
            }

            var updated = state switch
            {
                { LockedUntil: { } lockedUntil } when lockedUntil > now => state,
                _ when (state?.Failures ?? 0) + 1 >= MaximumFailures => new UnknownNameState(0, now + Duration, now),
                _ => new UnknownNameState((state?.Failures ?? 0) + 1, null, now),
            };
            _unknownNames[normalizedName] = updated;
            return updated.LockedUntil > now ? updated.LockedUntil : null;
        }
    }

    private void MakeRoomForOneMore(DateTimeOffset now)
    {
        if (_unknownNames.Count < MaximumTrackedUnknownNames)
        {
            return;
        }

        foreach (var expired in _unknownNames.Where(pair => pair.Value.LastFailure + Duration <= now).Select(pair => pair.Key).ToList())
        {
            _unknownNames.Remove(expired);
        }

        while (_unknownNames.Count >= MaximumTrackedUnknownNames)
        {
            _unknownNames.Remove(_unknownNames.MinBy(pair => pair.Value.LastFailure).Key);
        }
    }

    private sealed record UnknownNameState(int Failures, DateTimeOffset? LockedUntil, DateTimeOffset LastFailure);

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
