using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeEffectiveConfiguration(long? EffectiveRevision, string? AppliedBy, DateTimeOffset? AppliedAt);

internal sealed partial class RuntimeVersionStore
{
    /// <summary>
    /// 同一只读语句关联持久指针与应用元数据，不读/反序列化快照正文、不占写事务。
    /// 不能用最新历史或内存 activeRevision 代替；快照行缺失时仍保留有效版本号。
    /// </summary>
    public RuntimeEffectiveConfiguration ReadEffectiveConfiguration()
    {
        var row = database.Client.Queryable<SystemStateRecord>()
            .LeftJoin<RuntimeSnapshotRecord>((state, snapshot) => state.EffectiveRevision == snapshot.Revision)
            .Where((state, snapshot) => state.Id == SystemStateRecord.SingletonId)
            .Select((state, snapshot) => new { state.EffectiveRevision, snapshot.CreatedBy, snapshot.ActivatedAt })
            .Single() ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");
        var activated = row.ActivatedAt;
        return new RuntimeEffectiveConfiguration(row.EffectiveRevision, row.CreatedBy,
            activated is { } value && value >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && value <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null);
    }
}
