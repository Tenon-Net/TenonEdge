using System.Collections.Frozen;
using System.Collections.Immutable;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeVersionStore
{
    private readonly RuntimeOutputCatalog _outputCatalog = new();

    internal IReadOnlyDictionary<long, RuntimeOutputCatalogEntry> ReadOutputCatalog(long revision) =>
        _outputCatalog.Read(Load(revision) ?? throw new RuntimePlanDocumentException("运行快照不存在。"));
}

internal sealed record RuntimeOutputCatalogEntry(string? Key, string? Name, string Kind, ImmutableArray<RuntimeUnitId> Units);

/// <summary>只保留一份已验证的非秘密输出目录；每次仍从数据库加载快照并核对实际内容哈希。
/// 不持有内容正文、数据库客户端、事务或任何易变运行/安全状态。</summary>
internal sealed class RuntimeOutputCatalog
{
    private readonly Lock _gate = new();
    private CacheEntry? _last;
    private sealed record CacheEntry(long Revision, int SchemaVersion, string Hash,
        IReadOnlyDictionary<long, RuntimeOutputCatalogEntry> Targets);

    internal IReadOnlyDictionary<long, RuntimeOutputCatalogEntry> Read(RuntimeSnapshotRecord snapshot)
    {
        // 即使版本/存储哈希字段未改，内容损坏仍必须使本次查询失败，不能返回旧目录。
        RuntimeSnapshotDocument.VerifyIntegrity(snapshot);
        lock (_gate)
        {
            if (_last is { } previous && previous.Revision == snapshot.Revision &&
                previous.SchemaVersion == snapshot.SchemaVersion && previous.Hash == snapshot.ContentHash)
            { return previous.Targets; }
            var (plan, draft) = RuntimeSnapshotDocument.ReadForExecution(snapshot);
            var targets = plan.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal))
                .GroupBy(unit => unit.Configuration.GetProperty("connectionId").GetInt64()).ToFrozenDictionary(group => group.Key, group =>
                {
                    var connection = draft?.Connections.FirstOrDefault(item => item.Id == group.Key);
                    return new RuntimeOutputCatalogEntry(connection?.Key, connection?.Name, group.First().Id.Kind[7..],
                        [.. group.Select(unit => unit.Id)]);
                });
            _last = new(snapshot.Revision, snapshot.SchemaVersion, snapshot.ContentHash, targets);
            return targets;
        }
    }
}
