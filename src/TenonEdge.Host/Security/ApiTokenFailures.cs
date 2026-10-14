namespace TenonEdge.Host.Security;

/// <summary>令牌认证失败独立计数，不复用登录计数。单调一分钟窗口，有界来源集合；满时拒绝新来源而不逐出攻击计数。</summary>
internal sealed class ApiTokenFailures(TimeProvider time)
{
    internal const int MaximumSources = 4096;
    internal const int Limit = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WindowState> _sources = new(StringComparer.Ordinal);
    public Attempt? TryEnter(string ip, out int retryAfter)
    {
        lock (_gate)
        {
            Expire(ip);
            if (!_sources.TryGetValue(ip, out var state))
            {
                if (_sources.Count >= MaximumSources)
                {
                    foreach (var key in _sources.Where(pair => time.GetElapsedTime(pair.Value.Start) >= Window).Select(pair => pair.Key).ToArray())
                    { _sources.Remove(key); }
                }
                if (_sources.Count >= MaximumSources)
                { retryAfter = 60; return null; }
                state = new(time.GetTimestamp());
                _sources.Add(ip, state);
            }
            if (state.Failures + state.InFlight >= Limit)
            {
                retryAfter = Math.Max(1, (int)Math.Ceiling((Window - time.GetElapsedTime(state.Start)).TotalSeconds));
                return null;
            }
            state.InFlight++;
            retryAfter = 0;
            return new(this, ip, state);
        }
    }
    private void Expire(string ip)
    {
        if (_sources.TryGetValue(ip, out var state) && time.GetElapsedTime(state.Start) >= Window)
        { _sources.Remove(ip); }
    }
    private void Complete(string ip, WindowState state, bool failed)
    {
        lock (_gate)
        {
            state.InFlight--;
            if (failed)
            { state.Failures++; }
            if (state is { Failures: 0, InFlight: 0 } && _sources.GetValueOrDefault(ip) == state)
            { _sources.Remove(ip); }
        }
    }
    internal sealed class WindowState(long start)
    {
        public long Start { get; } = start;
        public int Failures { get; set; }
        public int InFlight { get; set; }
    }
    internal sealed class Attempt(ApiTokenFailures owner, string ip, WindowState state) : IDisposable
    {
        private int _disposed;
        public bool Failed { get; set; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { owner.Complete(ip, state, Failed); }
        }
    }
}
