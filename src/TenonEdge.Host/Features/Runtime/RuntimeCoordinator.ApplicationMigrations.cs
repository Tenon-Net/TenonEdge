using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    private void ValidateApplicationMigrations(PreparedPlan prepared, DeliveryMigrationIntentDraft[] intents,
        long? sourceRevision, RuntimeQuiescenceResult? quiet = null)
    {
        if (intents.Length == 0)
        { return; }
        if (sourceRevision is not { } previous || versions.ReadPointers().EffectiveRevision != previous)
        { throw new ApplicationRejectedException("configuration.migrationReviewChanged"); }
        foreach (var intent in intents)
        {
            var source = runtime.CaptureMigrationTarget(intent.Hold.ConnectionId, previous, quiet);
            var target = prepared.CaptureMigrationTarget(intent.Hold.ConnectionId);
            if (source?.Fingerprint != intent.Hold.OldFingerprint || source.Recipient != intent.Source || target != intent.Target)
            { throw new ApplicationRejectedException("configuration.migrationReviewChanged"); }
            // 预检不扩大为“整连接”或按页拆批；排空后再检查最终旧指纹范围。执行器仍在迁移事务中检查实际配额。
            var check = runtime.Deliveries.PreflightMigration(DeliveryMigrationCommand.OldTarget(intent.Hold.ConfigStoreId,
                intent.Hold.IntentId, intent.Hold.ConnectionId, intent.Hold.OldFingerprint, intent.Target.Fingerprint, intent.Target.Summary));
            if (check.Error is not null)
            { throw new ApplicationRejectedException(check.Error); }
        }
    }
}
