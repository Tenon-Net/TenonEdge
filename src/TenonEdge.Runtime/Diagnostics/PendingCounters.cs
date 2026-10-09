namespace TenonEdge.Runtime.Diagnostics;

/// <summary>只积累当前配置产生的计数增量；唯一写者提交成功后扣除快照，失败不清零。</summary>
internal sealed class PendingCounters
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly long[] _activity = new long[6];
    public bool HasValues { get { lock (_lock) { return _values.Count > 0; } } }

    public void RecordActivity(RuntimeActivity activity, long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        { _activity[(int)activity] = checked(_activity[(int)activity] + count); }
    }

    public RuntimeActivityTotals ActivitySnapshot()
    {
        lock (_lock)
        { return new(_activity[0], _activity[1], _activity[2], _activity[3], _activity[4], _activity[5]); }
    }

    public void Add(string name, long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        { return; }
        lock (_lock)
        {
            _values[name] = checked(_values.GetValueOrDefault(name) + count);
        }
    }

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_lock)
        { return new Dictionary<string, long>(_values, StringComparer.Ordinal); }
    }

    public void Committed(IReadOnlyDictionary<string, long> snapshot)
    {
        lock (_lock)
        {
            foreach (var (name, count) in snapshot)
            {
                var remaining = _values[name] - count;
                if (remaining < 0)
                { throw new InvalidOperationException("计数快照重复确认。"); }
                if (remaining == 0)
                { _values.Remove(name); }
                else
                { _values[name] = remaining; }
            }
        }
    }
}
