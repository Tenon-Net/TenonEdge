using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Diagnostics;

public sealed record CurrentFieldValue(string Key, FieldType? Type, string? Text, string Quality, string? Reason,
    bool IsStale, DateTimeOffset? CollectedAt, DateTimeOffset? ReceivedAt, TimeSource? TimeSource)
{
    public override string ToString() => $"Current field {Key} ({Quality}; value omitted)";
}
public sealed record CurrentValueCacheStatus(int Fields, long ChargedBytes, long Evictions);

/// <summary>独立于调试历史的当前值：只保留不可变标量及最后成功时间，不保留消息/原始 JSON。
/// 保守逻辑预算 16 MiB（每字段固定 1 KiB 加标量预算），单值最多 8 KiB；超限明确不可用。
/// 全局按最近更新淘汰，未访问/已淘汰字段返回 notRead，不伪造零值。只用于显示，绝不进入输出。
/// 版本切换在旧输入静默之后清空；同版本监督重建沿用状态，进程重启清空。</summary>
public sealed class CurrentValueCache(TimeProvider time)
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumValueBytes = 8 * 1024;
    private const int MetadataBytes = 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<RuntimeUnitId, Source> _sources = [];
    private readonly Dictionary<(RuntimeUnitId, string), LinkedListNode<Entry>> _fields = [];
    private readonly LinkedList<Entry> _order = [];
    private long? _revision;
    private long _charged;
    private long _evictions;

    internal void Activate(long revision, IEnumerable<RuntimeUnitId> inputs)
    {
        lock (_gate)
        {
            if (_revision == revision)
            { return; }
            _sources.Clear();
            _fields.Clear();
            _order.Clear();
            _charged = 0;
            _sources.TrimExcess();
            _fields.TrimExcess();
            _revision = revision;
            foreach (var id in inputs)
            { _sources.Add(id, new Source()); }
        }
    }

    internal void Capture(RuntimeUnitId id, long revision, EdgeMessage message)
    {
        lock (_gate)
        {
            if (_revision != revision || !_sources.TryGetValue(id, out var source))
            { return; }
            if (id.Key != $"{message.DeviceKey}/{message.InputId}")
            { throw new ArgumentException("当前值来源不符。"); }
            source.Sequence++;
            source.ReceivedAt = message.ReceivedAt;
            source.Stamp = time.GetTimestamp();
            source.PrivacyBlocked = message.DiagnosticAccess == DiagnosticSampleAccess.Redacted ||
                (message.DiagnosticAccess != DiagnosticSampleAccess.Safe &&
                    (message.RawJson is not null || id.Kind is "input.http" or "input.mqtt"));
            if (source.PrivacyBlocked)
            {
                // 新消息未通过隐私检查时不能继续展示该来源先前的值。
                var node = _order.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (node.Value.Id == id)
                    { Remove(node); }
                    node = next;
                }
                return;
            }
            foreach (var (key, value) in message.Values)
            { Put(key, value, null); }
            foreach (var (key, quality) in message.Quality)
            { Put(key, null, System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(quality.ToString())); }

            void Put(string key, FieldValue? value, string? reason)
            {
                // 配置字段有界；扩展不能通过任意长 key 绕过元数据预算。
                if (!BusinessKeys.IsValid(key))
                { return; }
                var identity = (id, key);
                _fields.TryGetValue(identity, out var previous);
                var good = value is not null;
                var oversized = value?.BudgetBytes > MaximumValueBytes;
                var retained = oversized ? null : value ?? previous?.Value.Value;
                var entry = new Entry(id, key, source.Sequence, retained, good ? message.CollectedAt : previous?.Value.CollectedAt,
                    good ? message.ReceivedAt : previous?.Value.ReceivedAt, good ? message.TimeSource : previous?.Value.TimeSource,
                    oversized ? "unavailable" : good ? "good" : "bad", oversized ? "valueTooLarge" : reason);
                var next = new LinkedListNode<Entry>(entry);
                if (previous is not null)
                { Remove(previous); }
                while (_charged + entry.Charge > MaximumBytes && _order.First is { } first)
                { Remove(first); _evictions++; }
                _fields.Add(identity, next);
                _order.AddLast(next);
                _charged += entry.Charge;
            }
        }
    }

    public CurrentFieldValue[] Read(RuntimeUnitId id, long revision, IEnumerable<string> keys, int? periodMs,
        bool readFailed, bool running)
    {
        lock (_gate)
        {
            var source = _revision == revision ? _sources.GetValueOrDefault(id) : null;
            // MQTT 不按时间判断新鲜度；轮询连续两个周期无新消息，或已明确失败/停止即过期。
            var expired = periodMs is { } period && (readFailed || !running || source?.ReceivedAt is not null &&
                time.GetElapsedTime(source.Stamp) >= TimeSpan.FromMilliseconds(2L * period));
            return keys.Select(key =>
            {
                if (source?.PrivacyBlocked == true)
                { return new CurrentFieldValue(key, null, null, "unavailable", "privacyBlocked", false, null, null, null); }
                var entry = source is null ? null : _fields.GetValueOrDefault((id, key))?.Value;
                if (entry is null)
                { return new CurrentFieldValue(key, null, null, "notRead", null, false, null, null, null); }
                var quality = entry.Sequence != source!.Sequence ? "missing" : entry.Quality;
                var stale = entry.Value is not null && (expired || quality != "good");
                return new CurrentFieldValue(key, entry.Value?.Type, entry.Value?.ToString(),
                    readFailed && periodMs is not null ? "bad" : quality,
                    readFailed && periodMs is not null ? "readFailed" : entry.Sequence != source.Sequence ? "missing" : entry.Reason,
                    stale, entry.CollectedAt, entry.ReceivedAt, entry.TimeSource);
            }).ToArray();
        }
    }

    public DateTimeOffset? LastReceived(RuntimeUnitId id, long revision)
    { lock (_gate) { return _revision == revision ? _sources.GetValueOrDefault(id)?.ReceivedAt : null; } }

    public CurrentValueCacheStatus Status
    { get { lock (_gate) { return new(_fields.Count, _charged, _evictions); } } }

    private void Remove(LinkedListNode<Entry> node)
    { _fields.Remove((node.Value.Id, node.Value.Key)); _order.Remove(node); _charged -= node.Value.Charge; }

    private sealed class Source
    {
        internal long Sequence;
        internal long Stamp;
        internal DateTimeOffset? ReceivedAt;
        internal bool PrivacyBlocked;
    }
    private sealed record Entry(RuntimeUnitId Id, string Key, long Sequence, FieldValue? Value, DateTimeOffset? CollectedAt,
        DateTimeOffset? ReceivedAt, TimeSource? TimeSource, string Quality, string? Reason)
    { internal long Charge => MetadataBytes + (Value?.BudgetBytes ?? 0); }
}
