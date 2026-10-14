using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Diagnostics;

public sealed record RuntimeMessageRecord(long Sequence, RuntimeUnitId ObjectId, long Revision, Guid SourceMessageId,
    DateTimeOffset CapturedAt, int OriginalBytes, int StoredBytes, bool Truncated, string? Body, bool PrivacyBlocked = false)
{
    public override string ToString() => $"Runtime message {Sequence} ({StoredBytes}/{OriginalBytes} bytes; body omitted)";
}
public sealed record RuntimeMessageCacheStatus(int Objects, int Records, long StoredBytes, long ChargedBytes);

/// <summary>只拥有捕获时裁剪的 UTF-8 副本，不持有原始消息/协议文档。采样与 UI 刷新无关。
/// 完整样本读取只提供复制的字节；调用者仍须核对输入正在运行、当前版本及载荷类型。
/// 元数据与索引以保守固定额度计入对象/全局预算，不允许小报文堆积出无限条目。</summary>
public sealed class RuntimeMessageCache
{
    public const int MaximumRecordBytes = 64 * 1024;
    public const int MaximumObjectBytes = 1024 * 1024;
    public const int MaximumGlobalBytes = 32 * 1024 * 1024;
    public const int MaximumObjectRecords = 100;
    internal const int MetadataCharge = 2048;

    private readonly TimeProvider _time;
    private readonly int _recordLimit;
    private int _objectLimit;
    private int _globalLimit;
    private int _countLimit;
    private readonly Lock _lock = new();
    private readonly Dictionary<RuntimeUnitId, Bucket> _objects = [];
    private readonly LinkedList<Entry> _ordered = [];
    private long _sequence;
    private long _charged;
    private long _stored;

    public RuntimeMessageCache(TimeProvider time) : this(time, MaximumRecordBytes, MaximumObjectBytes, MaximumGlobalBytes, MaximumObjectRecords) { }

    internal RuntimeMessageCache(TimeProvider time, int recordBytes, int objectBytes, int globalBytes, int recordsPerObject)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (recordBytes < 1 || recordBytes > MaximumRecordBytes || objectBytes < recordBytes + MetadataCharge ||
            objectBytes > MaximumObjectBytes || globalBytes < objectBytes || globalBytes > MaximumGlobalBytes ||
            recordsPerObject is < 1 or > MaximumObjectRecords)
        { throw new ArgumentException("调试缓存预算无效。"); }
        _time = time;
        _recordLimit = recordBytes;
        _objectLimit = objectBytes;
        _globalLimit = globalBytes;
        _countLimit = recordsPerObject;
    }

    /// <summary>原缓存内即时更新；只淘汰诊断副本，不改变业务通道或投递。
    /// 同一对象和全局都按捕获顺序移除；调大不恢复已淘汰数据。</summary>
    public void Configure(int objectBytes, int globalBytes, int recordsPerObject)
    {
        ValidateLimits(objectBytes, globalBytes, recordsPerObject);
        lock (_lock)
        {
            _objectLimit = objectBytes;
            _globalLimit = globalBytes;
            _countLimit = recordsPerObject;
            // 遍历全局链表，不复制所有对象/记录；移除的总次数不超过现存条数。
            var node = _ordered.First;
            while (node is not null)
            {
                var next = node.Next;
                var bucket = _objects[node.Value.ObjectId];
                if (bucket.Records.Count > _countLimit || bucket.Charged > _objectLimit)
                { Remove(node.Value); }
                node = next;
            }
            while (_charged > _globalLimit)
            { Remove(_ordered.First!.Value); }
        }
    }

    public static void ValidateLimits(int objectBytes, int globalBytes, int recordsPerObject)
    {
        if (objectBytes < MaximumRecordBytes + MetadataCharge || objectBytes > 64 * 1024 * 1024 ||
            globalBytes < objectBytes || globalBytes > 1024 * 1024 * 1024 || recordsPerObject is < 1 or > MaximumObjectRecords)
        { throw new ArgumentException("调试缓存每对象须为67584—67108864字节，全局须为每对象上限—1073741824字节，每对象须为1—100条；调高须在本机实测。"); }
    }

    internal long Capture(RuntimeUnitId objectId, long revision, Guid sourceMessageId, ReadOnlySpan<byte> body)
        => Capture(objectId, revision, sourceMessageId, body, body.Length);

    internal long CaptureInput(RuntimeUnitId objectId, long revision, EdgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(message);
        if (!objectId.Kind.StartsWith("input.", StringComparison.Ordinal) ||
            objectId.Key != $"{message.DeviceKey}/{message.InputId}" || revision <= 0)
        { throw new ArgumentException("输入诊断样本身份无效。"); }
        // 同一个缓存只允许一次编码，工作缓冲不会按输入数量无限放大。
        lock (_lock)
        {
            var privacyBlocked = message.DiagnosticAccess == DiagnosticSampleAccess.Redacted ||
                (message.DiagnosticAccess != DiagnosticSampleAccess.Safe &&
                    (message.RawJson is not null || objectId.Kind is "input.http" or "input.mqtt"));
            // 敏感/未检查消息只计完整编码长度，不分配或保存载荷前缀。
            var snapshot = InputMessageSnapshot.Encode(message, retainBody: !privacyBlocked);
            return Capture(objectId, revision, message.SourceMessageId, snapshot.Prefix, snapshot.OriginalBytes, privacyBlocked);
        }
    }

    internal long Capture(RuntimeUnitId objectId, long revision, Guid sourceMessageId, ReadOnlySpan<byte> body, int originalBytes, bool privacyBlocked = false)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (!(objectId.Kind.StartsWith("input.", StringComparison.Ordinal) || objectId.Kind.StartsWith("output.", StringComparison.Ordinal)) ||
            revision <= 0 || sourceMessageId == Guid.Empty || originalBytes < body.Length || !Utf8.IsValid(body))
        { throw new ArgumentException("调试记录身份、版本或 UTF-8 内容无效。"); }
        var length = privacyBlocked ? 0 : Math.Min(_recordLimit, body.Length);
        // length is the first excluded byte. Retreat to a rune boundary if it points into a sequence.
        while (length > 0 && length < body.Length && (body[length] & 0xc0) == 0x80)
        { length--; }
        var charge = MetadataCharge + length;
        lock (_lock)
        {
            var sequence = checked(_sequence + 1);
            // 在修改索引前分配载荷与两个节点。诊断分配失败可被输入通道降级，
            // 因此不能在只挂入一条链表后才申请另一节点，留下半插入记录。
            var entry = new Entry(sequence, objectId, revision, sourceMessageId, _time.GetUtcNow(), originalBytes, body[..length].ToArray(), privacyBlocked);
            entry.GlobalNode = new LinkedListNode<Entry>(entry);
            entry.ObjectNode = new LinkedListNode<Entry>(entry);
            if (_objects.TryGetValue(objectId, out var bucket))
            {
                while (bucket.Records.Count >= _countLimit || bucket.Charged + charge > _objectLimit)
                { Remove(bucket.Records.First!.Value); }
            }
            while (_charged + charge > _globalLimit)
            { Remove(_ordered.First!.Value); }
            // Eviction may have removed the now-empty bucket, including during global eviction.
            if (!_objects.TryGetValue(objectId, out bucket))
            { _objects.Add(objectId, bucket = new Bucket()); }
            _ordered.AddLast(entry.GlobalNode);
            bucket.Records.AddLast(entry.ObjectNode);
            bucket.Charged += charge;
            _charged += charge;
            _stored += length;
            _sequence = sequence;
            return sequence;
        }
    }

    /// <summary>最多一个对象的 100 条，按捕获次序从新到旧。includeBody=false 不解码/返回正文。</summary>
    public IReadOnlyList<RuntimeMessageRecord> Read(RuntimeUnitId objectId, bool includeBody)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(objectId, out var bucket))
            { return []; }
            return bucket.Records.Reverse().Select(entry => new RuntimeMessageRecord(entry.Sequence, entry.ObjectId,
                entry.Revision, entry.SourceMessageId, entry.At, entry.OriginalBytes, entry.Payload.Length,
                entry.Truncated, includeBody && !entry.PrivacyBlocked ? Encoding.UTF8.GetString(entry.Payload) : null, entry.PrivacyBlocked)).ToArray();
        }
    }

    /// <summary>淘汰/截断/对象或版本不符均拒绝；不得退回更旧的“完整”记录假冒选中的样本。</summary>
    internal bool TryReadComplete(RuntimeUnitId objectId, long sequence, long revision, Guid sourceMessageId, [NotNullWhen(true)] out byte[]? body)
    {
        lock (_lock)
        {
            var entry = _objects.GetValueOrDefault(objectId)?.Records.FirstOrDefault(item => item.Sequence == sequence);
            if (entry is null || entry.PrivacyBlocked || entry.Truncated || entry.Revision != revision || entry.SourceMessageId != sourceMessageId)
            { body = null; return false; }
            body = entry.Payload.ToArray();
            return true;
        }
    }

    /// <summary>成功切换后由生命周期所有者调用；调用前必须静默旧生产者，避免已删除对象再次捕获。</summary>
    internal void RetainObjects(IReadOnlySet<RuntimeUnitId> activeObjects)
    {
        ArgumentNullException.ThrowIfNull(activeObjects);
        lock (_lock)
        {
            var node = _ordered.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!activeObjects.Contains(node.Value.ObjectId))
                { Remove(node.Value); }
                node = next;
            }
        }
    }

    public RuntimeMessageCacheStatus Status
    {
        get { lock (_lock) { return new(_objects.Count, _ordered.Count, _stored, _charged); } }
    }

    private void Remove(Entry entry)
    {
        var bucket = _objects[entry.ObjectId];
        var charge = MetadataCharge + entry.Payload.Length;
        _ordered.Remove(entry.GlobalNode!);
        bucket.Records.Remove(entry.ObjectNode!);
        bucket.Charged -= charge;
        _charged -= charge;
        _stored -= entry.Payload.Length;
        if (bucket.Records.Count == 0)
        {
            _objects.Remove(entry.ObjectId);
            // Do not retain a high-water dictionary after deleting/evicting most objects.
            // This also keeps index overhead covered by the per-record metadata charge.
            if (_objects.Count == 0 || _objects.EnsureCapacity(0) > _objects.Count * 4)
            { _objects.TrimExcess(); }
        }
    }

    private sealed class Bucket
    {
        internal readonly LinkedList<Entry> Records = [];
        internal long Charged;
    }
    private sealed class Entry(long sequence, RuntimeUnitId objectId, long revision, Guid sourceMessageId,
        DateTimeOffset at, int originalBytes, byte[] payload, bool privacyBlocked)
    {
        internal long Sequence { get; } = sequence;
        internal RuntimeUnitId ObjectId { get; } = objectId;
        internal long Revision { get; } = revision;
        internal Guid SourceMessageId { get; } = sourceMessageId;
        internal DateTimeOffset At { get; } = at;
        internal int OriginalBytes { get; } = originalBytes;
        internal byte[] Payload { get; } = payload;
        internal bool PrivacyBlocked { get; } = privacyBlocked;
        internal bool Truncated => !PrivacyBlocked && Payload.Length != OriginalBytes;
        internal LinkedListNode<Entry>? GlobalNode;
        internal LinkedListNode<Entry>? ObjectNode;
    }
}
