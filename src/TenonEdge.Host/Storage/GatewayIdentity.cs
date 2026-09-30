using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 网关身份（总设计 §9.3、§10.4）：
/// <list type="bullet">
/// <item>NodeId：网关的本地身份，首次启动时生成，重启保持不变；不导出，恢复备份时采用备份中的值。</item>
/// <item>configStoreId：配置库首次创建时生成的随机标识，随备份恢复；queue.db 以它判断记录是否属于当前配置库（T1-03）。</item>
/// </list>
/// 两者都是随机 UUID，保存在 SystemState 中，以 SystemState 为准。
/// </summary>
internal sealed record GatewayIdentity(string NodeId, string ConfigStoreId)
{
    /// <summary>读取身份；配置库刚创建、尚未生成时在同一事务中生成并保存。</summary>
    public static GatewayIdentity LoadOrCreate(ConfigDatabase database, out bool created)
    {
        using var client = database.CreateIsolatedClient();
        client.Ado.BeginTran();
        try
        {
            var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
                ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");
            created = state.NodeId is null || state.ConfigStoreId is null;
            if (created)
            {
                state.NodeId ??= NewIdentifier();
                state.ConfigStoreId ??= NewIdentifier();
                client.Updateable(state).UpdateColumns(record => new { record.NodeId, record.ConfigStoreId }).ExecuteCommand();
            }

            client.Ado.CommitTran();
            return new GatewayIdentity(state.NodeId!, state.ConfigStoreId!);
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    private static string NewIdentifier() => Guid.NewGuid().ToString("D");
}
