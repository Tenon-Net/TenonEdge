using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>只有订阅协调时访问配置库；单例不持有连接/事务，网络等待不在数据库事务内。</summary>
internal sealed class MqttSubscriptionStore(ConfigDatabase database) : IMqttSubscriptionStore
{
    public IReadOnlyList<MqttSubscriptionState> Read(MqttSessionIdentity session)
    {
        using var client = database.CreateIsolatedClient();
        return client.Queryable<MqttSubscriptionRecord>()
            .Where(row => row.Recipient == session.Recipient && row.ClientId == session.ClientId)
            .ToList().Select(row => new MqttSubscriptionState(row.Filter, row.Qos, row.Confirmed)).ToArray();
    }

    public void Put(MqttSessionIdentity session, MqttSubscriptionState subscription)
    {
        using var client = database.CreateIsolatedClient();
        client.Ado.ExecuteCommand("""
            INSERT INTO mqtt_subscription (recipient, client_id, topic_filter, qos, confirmed)
            VALUES (@Recipient, @ClientId, @Filter, @Qos, @Confirmed)
            ON CONFLICT (recipient, client_id, topic_filter)
            DO UPDATE SET qos = excluded.qos, confirmed = excluded.confirmed
            """, new { session.Recipient, session.ClientId, subscription.Filter, subscription.Qos, subscription.Confirmed });
    }

    public void Remove(MqttSessionIdentity session, string filter)
    {
        using var client = database.CreateIsolatedClient();
        client.Deleteable<MqttSubscriptionRecord>()
            .Where(row => row.Recipient == session.Recipient && row.ClientId == session.ClientId && row.Filter == filter)
            .ExecuteCommand();
    }

    public void Clear(MqttSessionIdentity session)
    {
        using var client = database.CreateIsolatedClient();
        client.Deleteable<MqttSubscriptionRecord>()
            .Where(row => row.Recipient == session.Recipient && row.ClientId == session.ClientId).ExecuteCommand();
    }
}

[SugarTable("mqtt_subscription")]
internal sealed class MqttSubscriptionRecord
{
    [SugarColumn(ColumnName = "recipient", IsPrimaryKey = true)]
    public string Recipient { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "client_id", IsPrimaryKey = true)]
    public string ClientId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "topic_filter", IsPrimaryKey = true)]
    public string Filter { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "qos")]
    public int Qos { get; set; }
    [SugarColumn(ColumnName = "confirmed")]
    public bool Confirmed { get; set; }
}
