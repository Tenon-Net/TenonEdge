using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Host.Features.Routes;

/// <summary>只保留服务端该次预览生成的完整字节及非秘密配置，不保留原样本、草稿全集、数据库或凭证。</summary>
internal sealed record RouteTestSendArtifact(Guid Id, string Version, long SourceId, Guid SourceMessageId, DateTimeOffset CollectedAt,
    ImmutableArray<string> Path, JsonElement Configuration, RoutePreviewOutput Output, ReadOnlyMemory<byte> Payload, long BudgetBytes)
{
    public override string ToString() => "Route test artifact (body omitted)";
}

/// <summary>每次纯预览的有界暂存。仅显示出来的通用输出可确认；隐藏路径不额外保留报文。</summary>
internal sealed class RouteTestSendCapture
{
    internal const int MaximumCount = 128;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private readonly List<RouteTestSendArtifact> _items = [];
    internal IReadOnlyList<RouteTestSendArtifact> Items => _items;
    private long _bytes;

    internal bool TryAdd(Guid id, string version, long sourceId, Guid sourceMessageId, DateTimeOffset collectedAt,
        ImmutableArray<string> path, JsonElement configuration, RoutePreviewOutput output, byte[] payload)
    {
        if (output.Kind is not ("output.http" or "output.mqtt") || payload.Length is 0 or > StandardEnvelope.MaximumPayloadBytes)
        { return false; }
        // 元数据UTF-8/JSON文档、UTF-16字符串、集合/对象固定开销均计入保守上界。
        // 不保留展示正文的第二份字符串，只保留完整字节；哈希在准备确认时计算。
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { version, path, configuration, output = output with { Body = "" } });
        var bytes = 4096L + payload.Length + metadata.Length * 4L;
        if (_items.Count >= MaximumCount || bytes > 1024 * 1024 || _bytes + bytes > MaximumBytes)
        { return false; }
        _items.Add(new(id, version, sourceId, sourceMessageId, collectedAt, path, configuration.Clone(),
            output with { Body = "", Headers = output.Headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) }, payload.ToArray(), bytes));
        _bytes += bytes;
        return true;
    }
}

internal sealed record RouteTestSendReview(Guid Id, Guid ConfirmationId, string Version, string Kind, long ConnectionId,
    string Recipient, string Authentication, string? Username, string Address, int? Qos, Guid DeliveryId, Guid SourceMessageId,
    DateTimeOffset CollectedAt, int BodyBytes, bool PreviewTruncated, string Sha256, DateTimeOffset ExpiresAt);
internal sealed record RouteTestSendSelection(RouteTestSendArtifact Artifact, RouteTestSendReview Review,
    HttpCredentialIdentity? HttpIdentity, MqttCredentialIdentity? MqttIdentity);
internal sealed class RouteTestSendException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>会话所属、单调过期、确认一次消费的内存产物。每会话仅最新一次预览；全局条数/字节均有界。
/// 不排队或淘汰其他会话；容量不足只禁用本次测试发送，纯预览仍返回。宿主重启即失效。</summary>
internal sealed class RouteTestSendArtifacts : IDisposable
{
    internal const int MaximumCount = 256;
    internal const long MaximumBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private long _bytes;
    private bool _disposed;

    public RouteTestSendArtifacts(TimeProvider time)
    {
        _time = time;
        _timer = time.CreateTimer(_ => { lock (_gate) { Expire(); } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal bool Publish(string owner, RouteTestSendCapture capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > 256)
        { throw new ArgumentException("会话标识无效。"); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            foreach (var id in _entries.Where(pair => pair.Value.Owner == owner).Select(pair => pair.Key).ToArray())
            { Remove(id); }
            var bytes = capture.Items.Sum(item => item.BudgetBytes);
            if (_entries.Count + capture.Items.Count > MaximumCount || _bytes + bytes > MaximumBytes)
            { return false; }
            var timestamp = _time.GetTimestamp();
            var expiry = _time.GetUtcNow() + Lifetime;
            foreach (var item in capture.Items)
            { _entries.Add(item.Id, new(owner, timestamp, expiry, item)); }
            _bytes += bytes;
            return true;
        }
    }

    internal RouteTestSendArtifact Read(Guid id, string owner)
    { lock (_gate) { return Require(id, owner).Artifact; } }

    internal RouteTestSendReview Prepare(Guid id, string owner, RouteTestSendArtifact expected, long connectionId,
        string recipient, string authentication, string? username, HttpCredentialIdentity? http, MqttCredentialIdentity? mqtt)
    {
        lock (_gate)
        {
            var entry = Require(id, owner);
            if (!ReferenceEquals(entry.Artifact, expected))
            { throw Missing(); }
            var output = expected.Output;
            var review = new RouteTestSendReview(id, Guid.NewGuid(), expected.Version, output.Kind, connectionId, recipient,
                authentication, username, output.Address, output.Qos, output.DeliveryId, expected.SourceMessageId, expected.CollectedAt,
                expected.Payload.Length, output.Truncated, Convert.ToHexStringLower(SHA256.HashData(expected.Payload.Span)), entry.ExpiresAt);
            entry.Selection = new(expected, review, http, mqtt);
            return review;
        }
    }

    internal RouteTestSendSelection Consume(Guid id, Guid confirmationId, string owner, bool confirmed)
    {
        if (!confirmed || confirmationId == Guid.Empty)
        { throw new ArgumentException("须明确确认原目标、完整报文及真实发送风险。"); }
        lock (_gate)
        {
            var entry = Require(id, owner);
            if (entry.Selection is not { } selected || selected.Review.ConfirmationId != confirmationId)
            { throw Missing(); }
            Remove(id); // 网络、审计或前置检查失败亦不恢复确认，不能因丢回复而重放。
            return selected;
        }
    }

    private Entry Require(Guid id, string owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Expire();
        return _entries.TryGetValue(id, out var entry) && entry.Owner == owner ? entry : throw Missing();
    }
    private void Expire()
    {
        foreach (var id in _entries.Where(pair => _time.GetElapsedTime(pair.Value.Created) >= Lifetime).Select(pair => pair.Key).ToArray())
        { Remove(id); }
    }
    private void Remove(Guid id) { _bytes -= _entries[id].Artifact.BudgetBytes; _entries.Remove(id); }
    private static RouteTestSendException Missing() => new(404, "testSend.notFound");
    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        { _disposed = true; _entries.Clear(); _bytes = 0; }
    }
    private sealed class Entry(string owner, long created, DateTimeOffset expiresAt, RouteTestSendArtifact artifact)
    {
        public string Owner { get; } = owner;
        public long Created { get; } = created;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public RouteTestSendArtifact Artifact { get; } = artifact;
        public RouteTestSendSelection? Selection { get; set; }
    }
}
