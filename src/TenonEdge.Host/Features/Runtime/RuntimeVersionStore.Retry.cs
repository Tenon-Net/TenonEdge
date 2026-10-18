using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeRetryCheckpoint(RuntimeSnapshotRecord Snapshot, RuntimePlanDocument Plan);

internal sealed partial class RuntimeVersionStore
{
    // 重试当前不可变有效快照，不读取/编译未应用草稿，不尝试修复或跳过损坏哈希。
    public RuntimeRetryCheckpoint ReadRetryCheckpoint(long expectedRevision, string expectedPlanHash)
    {
        var snapshot = database.InTransaction(client =>
        {
            var state = State(client);
            if (state.EffectiveRevision != expectedRevision)
            { throw new DraftWriteException("configuration.reviewChanged"); }
            if (state.PendingRevision is not null)
            { throw new DraftWriteException("runtime.applicationInProgress"); }
            return RequiredSnapshot(client, expectedRevision);
        });
        var plan = RuntimeSnapshotDocument.ReadExecutionPlan(snapshot);
        if (plan.ContentHash != expectedPlanHash)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        plan.ValidateTransforms(transforms);
        plan.ValidateInputs(inputModules);
        plan.ValidateOutputs(outputModules);
        return new(snapshot, plan);
    }

    // 清零与激活尝试审计、运行中标记同事务；先落盘再构建，构建过程中再次崩溃仍计入启动保护。
    public void MarkRetry(RuntimeRetryCheckpoint checkpoint, AuditAccount account, string? sourceIp) => database.InTransaction(client =>
    {
        var snapshot = checkpoint.Snapshot;
        var state = State(client);
        if (state.EffectiveRevision != snapshot.Revision)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        if (state.PendingRevision is not null)
        { throw new DraftWriteException("runtime.applicationInProgress"); }
        var current = RequiredSnapshot(client, snapshot.Revision);
        if (current.Content != snapshot.Content || current.ContentHash != snapshot.ContentHash || current.SchemaVersion != snapshot.SchemaVersion)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        client.Updateable<RuntimeSnapshotRecord>().SetColumns(row => row.AbnormalExitCount == 0)
            .Where(row => row.Revision == snapshot.Revision).ExecuteCommand();
        state.RunningRevision = snapshot.Revision;
        client.Updateable(state).UpdateColumns(row => row.RunningRevision).ExecuteCommand();
        audit.Append(client, new(AuditActions.RuntimeRetried, AuditResults.Success, account, sourceIp, $"r{snapshot.Revision}",
            "人工重试：清零非正常退出次数并尝试激活当前有效快照；不应用草稿、不生成版本，健康结果见操作与运行状态。"));
        return true;
    });
}
