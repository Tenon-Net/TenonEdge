using System.Buffers;
using TenonEdge.Host.Features.Audit;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal sealed record ConfigurationImportReview(Guid Id, string Version, ConfigurationImportMode Mode,
    ConfigurationChange[] Changes, ConfigurationImportCredentialDecision[] Credentials, DateTimeOffset ExpiresAt);
internal sealed class ConfigurationImportRequestException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>全网关最多一份待确认导入；会话归属、单调过期、确认一次消费。
/// 调用方先取共享预览预算，本服务再准入读取上传流，不让并发大上传先物化再排队。</summary>
internal sealed class ConfigurationImportPreparation(ConfigurationImportStore store, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private Prepared? _current;
    private bool _working;

    public async Task<ConfigurationImportReview> PrepareAsync(Stream file, ConfigurationImportMode mode, string expectedVersion,
        string owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateOwner(owner);
        lock (_gate)
        {
            Expire();
            if (_working || _current is { } current && current.Owner != owner)
            { throw Error(409, "configuration.importBusy"); }
            _working = true;
            // A failed replacement upload must not leave an earlier, now invisible
            // confirmation actionable under an old browser tab.
            _current = null;
        }
        try
        {
            using var deadline = new CancellationTokenSource(UploadTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var bytes = await ReadFileAsync(file, linked.Token);
            var candidate = store.Prepare(bytes, mode, expectedVersion, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var review = new ConfigurationImportReview(Guid.NewGuid(), candidate.Draft.Version, mode, candidate.Changes,
                candidate.Credentials, time.GetUtcNow() + Lifetime);
            lock (_gate)
            { _current = new(owner, time.GetTimestamp(), review, candidate); }
            return review;
        }
        finally { lock (_gate) { _working = false; } }
    }

    public ConfigurationImportReview? Current(string owner)
    {
        lock (_gate)
        {
            Expire();
            return _current is { } current && current.Owner == owner ? current.Review : null;
        }
    }

    public void Cancel(Guid id, string owner)
    {
        lock (_gate)
        {
            _ = Require(id, owner);
            _current = null;
        }
    }

    public DraftVersion Confirm(Guid id, bool confirmed, string owner, AuditAccount account, string? sourceIp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!confirmed)
        { throw new ArgumentException("须明确确认导入模式、完整变更及凭证重填清单。"); }
        Prepared selected;
        lock (_gate)
        {
            selected = Require(id, owner);
            _current = null;
            _working = true;
        }
        try
        { return store.Commit(selected.Candidate, account, sourceIp, token); }
        finally { lock (_gate) { _working = false; } }
    }

    private Prepared Require(Guid id, string owner)
    {
        Expire();
        if (_current is not { } current || current.Owner != owner || current.Review.Id != id)
        { throw Error(404, "configuration.importNotFound"); }
        if (_working)
        { throw Error(409, "configuration.importBusy"); }
        return current;
    }
    private void Expire()
    {
        if (_current is { } current && time.GetElapsedTime(current.Timestamp) >= Lifetime)
        { _current = null; }
    }
    private static void ValidateOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256)
        { throw new ArgumentException("导入须绑定有效的管理会话。"); }
    }
    private static ConfigurationImportRequestException Error(int status, string code) => new(status, code);
    private sealed record Prepared(string Owner, long Timestamp, ConfigurationImportReview Review, ConfigurationImportCandidate Candidate);

    private static async Task<byte[]> ReadFileAsync(Stream source, CancellationToken token)
    {
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                // Read at most one byte beyond the actual file limit, even for a
                // chunked/non-seekable stream. Never truncate and import a prefix.
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length,
                    ConfigurationExport.MaximumBytes - destination.Length + 1)), token);
                if (count == 0)
                { return destination.ToArray(); }
                if (destination.Length + count > ConfigurationExport.MaximumBytes)
                { throw ConfigurationImportDocument.Error("import.fileTooLarge", "$"); }
                destination.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
