using System.Text.Json;
using TenonEdge.Host.Features.Configuration;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record ConfigurationVersionDetail(RuntimeVersionSummary Version, string ContentHash, string PlanHash,
    ConfigurationCheckpoint? Draft, JsonElement Plan, string[] Warnings);

internal sealed record ConfigurationVersionComparison(long BeforeRevision, long? AfterRevision, string? DraftVersion,
    ConfigurationChange[] Changes, ConfigurationPointSummary[] Points);

internal sealed partial class RuntimeVersionStore
{
    public ConfigurationVersionDetail ReadVersion(long revision, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        token.ThrowIfCancellationRequested();
        var snapshot = Load(revision) ?? throw new KeyNotFoundException("配置版本不存在或已清理。");
        var parsed = RuntimeSnapshotDocument.Read(snapshot);
        token.ThrowIfCancellationRequested();
        using var plan = JsonDocument.Parse(parsed.Plan.Canonical);
        return new(new(snapshot.Revision, snapshot.SchemaVersion, snapshot.State, snapshot.Origin, snapshot.OriginDetail,
            snapshot.CreatedBy, snapshot.CreatedAt, snapshot.ActivatedAt, snapshot.IsStable, snapshot.NotEffectiveReason, snapshot.ChangeDescription),
            snapshot.ContentHash, parsed.Plan.ContentHash, parsed.Draft, plan.RootElement.Clone(),
            parsed.Draft is null ? ["configuration.legacyComparisonUnavailable"] : []);
    }

    // 同一事务捕获双方；事务外校验内容哈希和比较，不读当前凭证或编译/准备运行单元。
    public ConfigurationVersionComparison CompareVersionContents(long beforeRevision, long afterRevision, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beforeRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(afterRevision);
        token.ThrowIfCancellationRequested();
        var pair = database.InTransaction(client => (Before: RequiredSnapshot(client, beforeRevision), After: RequiredSnapshot(client, afterRevision)));
        var before = RequiredDraft(pair.Before);
        var after = RequiredDraft(pair.After);
        token.ThrowIfCancellationRequested();
        return new(beforeRevision, afterRevision, null, ConfigurationDiff.Between(before, after), ConfigurationPointSummary.Between(before, after));
    }

    public ConfigurationVersionComparison CompareVersionToDraft(long revision, string expectedVersion, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length > 64)
        { throw new ArgumentException("须提供有效草稿版本。", nameof(expectedVersion)); }
        token.ThrowIfCancellationRequested();
        var pair = database.InTransaction(client => (Before: RequiredSnapshot(client, revision), After: ConfigurationCheckpoint.Read(client, expectedVersion)));
        var before = RequiredDraft(pair.Before);
        token.ThrowIfCancellationRequested();
        return new(revision, null, pair.After.Version, ConfigurationDiff.Between(before, pair.After), ConfigurationPointSummary.Between(before, pair.After));
    }
}
