using SqlSugar;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Configuration;

/// <summary>完整非秘密草稿，包括停用和未引用对象。只记录凭证所依附的连接 ID，不含凭证内容、运行状态或平台绑定。</summary>
internal sealed record ConfigurationCheckpoint(string Version, ConnectionDraft[] Connections, DeviceDraft[] Devices,
    InputDraft[] Inputs, RouteDraft[] Routes, long[]? CredentialConnections = null, ConfigurationEdit[]? Edits = null)
{
    // 调用方持有配置事务；先限定总字节，再物化行。编译及快照序列化在事务外进行。
    public static ConfigurationCheckpoint Read(SqlSugarClient client, string expectedVersion)
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var bytes = client.Ado.GetLong("""
            SELECT (SELECT COALESCE(SUM(length(CAST(content AS BLOB))), 0) FROM connection_draft)
                 + (SELECT COALESCE(SUM(length(CAST(content AS BLOB))), 0) FROM device_draft)
                 + (SELECT COALESCE(SUM(length(CAST(content AS BLOB))), 0) FROM input_draft)
                 + (SELECT COALESCE(SUM(length(CAST(graph AS BLOB)) + length(CAST(name AS BLOB))
                     + length(CAST(COALESCE(description, '') AS BLOB))), 0) FROM route_draft)
                 + (SELECT COALESCE(SUM(length(CAST(COALESCE(updated_by, '') AS BLOB)) + 192), 0) FROM configuration_edit)
            """);
        if (bytes > RuntimePlanDocument.MaximumBytes)
        { throw new RuntimePlanDocumentException("完整配置快照超过 16 MiB。"); }
        var connections = client.Queryable<ConnectionDraftRecord>().OrderBy(row => row.Id).ToList().Select(ConnectionDraftStore.Decode).ToArray();
        var connectionIds = connections.Select(connection => connection.Id).ToHashSet();
        return new(expectedVersion,
            connections,
            client.Queryable<DeviceDraftRecord>().OrderBy(row => row.Id).ToList().Select(DeviceDraftStore.Decode).ToArray(),
            client.Queryable<InputDraftRecord>().OrderBy(row => row.Id).ToList().Select(InputDraftStore.Decode).ToArray(),
            client.Queryable<RouteDraftRecord>().OrderBy(row => row.Id).ToList().Select(RouteDraftStore.Decode).ToArray(),
            client.Queryable<MqttCredentialRecord>().Select(row => row.ConnectionId).ToList()
                .Concat(client.Queryable<HttpCredentialRecord>().Select(row => row.ConnectionId).ToList()).Where(connectionIds.Contains).Distinct().Order().ToArray(),
            ConfigurationEditJournal.Read(client));
    }
}
