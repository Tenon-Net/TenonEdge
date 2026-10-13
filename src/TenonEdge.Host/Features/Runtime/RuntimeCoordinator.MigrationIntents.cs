using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    /// <summary>启动/人工重试在放行任何发送者之前补齐已提交意图的隔离。先读配置分页，再交给唯一队列写者；
    /// 不跨库持有事务。隔离收据使重复恢复不扩大原范围；尚不执行迁移、不解除暂停。</summary>
    private async Task HoldWaitingMigrationIntentsAsync(RuntimeQuiescenceResult? quiet = null)
    {
        long revision = 0, connection = 0;
        while (true)
        {
            var page = migrations.ReadWaitingIntents(revision, connection);
            if (page.Length == 0)
            { return; }
            runtime.StartCore();
            foreach (var intent in page)
            {
                DeliveryMigrationHold hold;
                try
                { hold = intent.ToHold(); }
                catch (Exception error) when (error is ArgumentException or FormatException)
                { throw new DeliveryStoreException("迁移意图的原身份无效，不能放行发送者。"); }
                var existing = runtime.Deliveries.ReadMigrationHold(hold.IntentId);
                if (existing is not null)
                {
                    if (existing.RequestHash != hold.RequestHash)
                    { throw new DeliveryStoreException("迁移隔离收据与配置意图不一致，不能放行发送者。"); }
                    continue;
                }
                var result = await runtime.HoldMigrationAsync(hold, quiet?.Revision is null ? null : quiet, CancellationToken.None);
                if (!result.Committed)
                { throw new DeliveryStoreException("已提交迁移意图的旧记录尚未完成持久隔离；保持发送停止，修复存储后重试。"); }
            }
            revision = page[^1].Revision;
            connection = page[^1].ConnectionId;
        }
    }
}
