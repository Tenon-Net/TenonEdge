using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record ConfigurationReview(string Version, long? EffectiveRevision, bool ComparisonAvailable,
    ConfigurationChange[] Changes, ConfigurationPointSummary[] Points, RouteTopologyIssue[] RouteIssues, RuntimeTargetChangeBlock[] TargetBlocks,
    string? PlanHash, string[] Warnings)
{
    // 仅静态检查，不承诺之后准备/静默/提交必然成功；队列及草稿仍可变化。
    public bool PreflightPassed => ComparisonAvailable && PlanHash is not null && RouteIssues.Length == 0 && TargetBlocks.Length == 0;
}

internal sealed record RuntimeTargetChangeBlock(long ConnectionId, string Kind, string Recipient, string Address,
    bool HasPending, bool HasFailed);

internal sealed partial class RuntimeVersionStore
{
    /// <summary>事务内读取同一时刻的完整草稿、有效快照和凭证引用；事务外比较/编译。
    /// 不冻结、不准备、不改变草稿、版本或队列。调用者须限制并发，并按确认版本再次执行应用。</summary>
    public ConfigurationReview ReviewDraft(string expectedDraftVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expectedDraftVersion) || expectedDraftVersion.Length > 64)
        { throw new ArgumentException("须提供有效草稿版本。", nameof(expectedDraftVersion)); }
        var captured = database.InTransaction(client =>
        {
            var draft = ConfigurationCheckpoint.Read(client, expectedDraftVersion);
            var effective = State(client).EffectiveRevision;
            return (Draft: draft, Effective: effective is { } revision ? RequiredSnapshot(client, revision) : null,
                MqttCredentials: client.Queryable<MqttCredentialRecord>().Select(row => row.ConnectionId).ToList().ToHashSet());
        });
        token.ThrowIfCancellationRequested();
        // 损坏快照不能假装为空；让调用方报告存储/快照故障，禁止确认。
        (RuntimePlanDocument Plan, ConfigurationCheckpoint? Draft)? previous = captured.Effective is null ? null : RuntimeSnapshotDocument.Read(captured.Effective);
        var before = captured.Effective is null ? new ConfigurationCheckpoint("0", [], [], [], []) : previous!.Value.Draft;
        var warnings = new List<string>
        {
            "configuration.allInputsPause",
            "configuration.mqttQos1RequiresPersistentBrokerSession",
            "configuration.qos0AndPollingGap",
            "configuration.queuedMessagesKeepOriginalContent",
        };
        if (before is null)
        { warnings.Add("configuration.legacyComparisonUnavailable"); }
        var snapshot = new RouteCompilationSnapshot(captured.Draft.Version, captured.Draft.Routes,
            captured.Draft.Devices.ToDictionary(value => value.Id), captured.Draft.Inputs.ToDictionary(value => value.Id),
            captured.Draft.Connections.ToDictionary(value => value.Id), captured.MqttCredentials);
        var issues = new List<RouteTopologyIssue>();
        RuntimePlanDocument? plan = null;
        try
        { plan = RoutePlanCompiler.Compile(snapshot, token).Document; }
        catch (RouteTopologyException error)
        {
            issues.Add(error.Issue);
            // 总编译遇到首错即停止；逐路线补充定位，使一次确认能列出所有有错误的路线。
            foreach (var route in snapshot.Routes.Where(route => route.Enabled).OrderBy(route => route.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                { _ = RoutePlanCompiler.Compile(snapshot with { Routes = [route] }, token); }
                catch (RouteTopologyException routeError) { issues.Add(routeError.Issue); }
            }
        }
        var blocks = plan is null || captured.Effective is null ? []
            : TargetChangeBlocks(previous!.Value.Plan, plan, token);
        if (blocks.Length != 0)
        { warnings.Add("configuration.targetChangeRequiresDrainClearOrMigration"); }
        token.ThrowIfCancellationRequested();
        return new(captured.Draft.Version, captured.Effective?.Revision, before is not null,
            before is null ? [] : ConfigurationDiff.Between(before, captured.Draft),
            before is null ? [] : ConfigurationPointSummary.Between(before, captured.Draft), issues.Distinct().ToArray(),
            blocks, plan?.ContentHash, warnings.ToArray());
    }

    private RuntimeTargetChangeBlock[] TargetChangeBlocks(RuntimePlanDocument previous, RuntimePlanDocument next, CancellationToken token)
    {
        var current = OutputRecipients(next).ToHashSet();
        var blocked = new List<RuntimeTargetChangeBlock>();
        foreach (var old in OutputRecipients(previous).Distinct().OrderBy(value => value.ConnectionId).ThenBy(value => value.Address, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (current.Contains(old))
            { continue; }
            var pending = deliveries.ReadPending(old.ConnectionId, 0, 1).Count > 0;
            var failed = deliveries.ReadFailed(old.ConnectionId, 0, 1).Count > 0;
            if (pending || failed)
            { blocked.Add(new(old.ConnectionId, old.Kind, old.Recipient, old.Address, pending, failed)); }
        }
        return blocked.ToArray();
    }
}
