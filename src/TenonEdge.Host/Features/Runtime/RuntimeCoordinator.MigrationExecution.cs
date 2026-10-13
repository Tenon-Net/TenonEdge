using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    internal static readonly TimeSpan MigrationPollInterval = TimeSpan.FromSeconds(5);
    private long _migrationRevisionCursor;
    private long _migrationConnectionCursor;

    /// <summary>由唯一后台服务有界轮询。等待中的意图不领取应用互斥；只在目标可核对后领取一次队列操作。
    /// 每轮最多一项，不创建客户端或第二个运行时，停机仍等待同一 _deliveryWrite。</summary>
    private async Task ExecuteWaitingMigrationIntentAsync()
    {
        try
        {
            long revision;
            lock (_lock)
            {
                if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                    _state != RuntimeCoordinatorState.Active || _activeRevision is not { } active)
                { return; }
                revision = active;
            }
            var page = migrations.ReadExecutableIntents(_migrationRevisionCursor, _migrationConnectionCursor);
            if (page.Length == 0)
            {
                _migrationRevisionCursor = _migrationConnectionCursor = 0;
                return;
            }
            foreach (var intent in page)
            {
                _migrationRevisionCursor = intent.Revision;
                _migrationConnectionCursor = intent.ConnectionId;
                if (intent.Revision > revision || versions.ReadPointers().EffectiveRevision != revision)
                { continue; }
                var target = runtime.CaptureMigrationTarget(intent.ConnectionId, revision);
                // 没有有效发送者或 MQTT 凭证尚不可描述时继续等待；不从草稿取得身份、不探测网络。
                if (target is null || !target.Recipient.AccountRecorded)
                { continue; }
                var source = OutputRecipientIdentity.Read(intent.SourceSummary);
                var approved = OutputRecipientIdentity.Read(intent.TargetSummary);
                var error = source?.Protocol != intent.Protocol || approved?.Protocol != intent.Protocol ||
                    target.Recipient.Protocol != intent.Protocol ? DeliveryMigrationErrors.ProtocolMismatch
                    : approved.Address != intent.TargetRecipient || target.Recipient != approved ||
                        target.Fingerprint != intent.TargetFingerprint ? DeliveryMigrationErrors.TargetChanged : null;
                var command = DeliveryMigrationCommand.ForIntent(intent.ToHold(), Guid.NewGuid(), intent.TargetFingerprint!, intent.TargetSummary!);
                TaskCompletionSource completion;
                lock (_lock)
                {
                    if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                        _state != RuntimeCoordinatorState.Active || _activeRevision != revision)
                    { return; }
                    completion = NewSignal();
                    _deliveryWrite = completion;
                }
                // 执行接受后与 HTTP 请求无关；后台服务的取消不能让它在队列 COMMIT 前误报中断。
                await RunMigrationOwnedAsync(command, revision, intent.SourceSummary, intent.AccountName, intent.SourceIp,
                    completion, intent, error);
                return;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 单次读库或损坏资料不能终止稳定计时/宿主，也不能触发快速重试或放行旧记录。
            runtimeEvents.Record(RuntimeEventLevel.Warning, "delivery.migrationIntentUnavailable", null,
                "等待意图尚不能核对；保留隔离，稍后再检查配置与存储。尚未新增迁移执行。");
        }
    }
}
