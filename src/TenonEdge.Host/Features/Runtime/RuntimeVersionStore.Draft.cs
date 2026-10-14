using System.Text.Json;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record ConfigurationDraftSummary(string Version, long? EffectiveRevision, long? PendingRevision,
    bool ComparisonAvailable, int? ChangeCount, ConfigurationObjectCount[] Objects, ConfigurationPointSummary[] Points, string[] Warnings);
internal sealed record ConfigurationObjectCount(string Kind, int Added, int Modified, int Removed);
internal sealed record ConfigurationDiscardResult(string Version, long? EffectiveRevision, string[] Warnings);

internal sealed partial class RuntimeVersionStore
{
    public ConfigurationDraftSummary ReadDraftSummary(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = database.InTransaction(client =>
        {
            var version = ConfigurationDraft.ReadVersion(client);
            var state = State(client);
            return (Draft: ConfigurationCheckpoint.Read(client, version), state.PendingRevision,
                Effective: state.EffectiveRevision is { } revision ? RequiredSnapshot(client, revision) : null);
        });
        token.ThrowIfCancellationRequested();
        var before = captured.Effective is null ? EmptyDraft() : RuntimeSnapshotDocument.Read(captured.Effective).Draft;
        if (before is null)
        { return new(captured.Draft.Version, captured.Effective?.Revision, captured.PendingRevision, false, null, [], [], ["configuration.legacyComparisonUnavailable"]); }
        var changes = ConfigurationDiff.Between(before, captured.Draft);
        var counts = changes.GroupBy(change => change.Kind).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ConfigurationObjectCount(group.Key, group.Count(change => change.Change == "added"),
                group.Count(change => change.Change == "modified"), group.Count(change => change.Change == "removed"))).ToArray();
        return new(captured.Draft.Version, captured.Effective?.Revision, captured.PendingRevision, true,
            changes.Length, counts, ConfigurationPointSummary.Between(before, captured.Draft), []);
    }

    /// <summary>只替换非秘密草稿，不应用运行计划。事务外准备行，事务内复核确认版本、替换、推进草稿版本及记审计。
    /// pending 冻结后拒绝放弃；尚未冻结的已准入应用会因本次推进草稿版本而安全失败。</summary>
    public ConfigurationDiscardResult DiscardDraft(string expectedVersion, long? expectedEffectiveRevision,
        AuditAccount account, string? sourceIp, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length > 64 || expectedEffectiveRevision is <= 0)
        { throw new ArgumentException("须提供已确认的草稿与有效版本。"); }
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Name);
        token.ThrowIfCancellationRequested();
        var captured = database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, expectedVersion);
            var state = State(client);
            CheckDiscardState(state.EffectiveRevision, state.PendingRevision);
            return state.EffectiveRevision is { } revision ? RequiredSnapshot(client, revision) : null;
        });
        var draft = captured is null ? EmptyDraft() : RequiredDraft(captured);
        var replacement = new ConfigurationReplacement(draft);
        token.ThrowIfCancellationRequested();
        return database.InTransaction(client =>
        {
            token.ThrowIfCancellationRequested();
            ConfigurationDraft.CheckVersion(client, expectedVersion);
            var state = State(client);
            CheckDiscardState(state.EffectiveRevision, state.PendingRevision);
            if (captured is not null && RequiredSnapshot(client, captured.Revision).ContentHash != captured.ContentHash)
            { throw new DraftWriteException("configuration.reviewChanged"); }
            if (!replacement.Apply(client, expectedVersion, time))
            { throw new DraftWriteException("configuration.draftConflict"); }
            var version = ConfigurationDraft.ReadVersion(client);
            audit.Append(client, new(AuditActions.DraftDiscarded, AuditResults.Success, account, sourceIp, "configuration.draft",
                JsonSerializer.Serialize(new { previousVersion = expectedVersion, version, effectiveRevision = expectedEffectiveRevision })));
            return new ConfigurationDiscardResult(version, expectedEffectiveRevision, ["configuration.discardDoesNotRestoreCredentials"]);
        });

        void CheckDiscardState(long? effective, long? pending)
        {
            if (effective != expectedEffectiveRevision)
            { throw new DraftWriteException("configuration.reviewChanged"); }
            if (pending is not null)
            { throw new DraftWriteException("runtime.applicationInProgress"); }
        }
    }

    private static ConfigurationCheckpoint EmptyDraft() => new("0", [], [], [], [], [], []);
}
