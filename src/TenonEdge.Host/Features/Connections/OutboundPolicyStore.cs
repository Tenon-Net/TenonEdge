using System.Net;
using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Host.Features.Connections;

/// <summary>白名单不是运行快照的一部分。每次连接读取当前值，更新与审计同一配置库事务。</summary>
internal sealed class OutboundPolicyStore(ConfigDatabase database, AuditTrail audit, TimeProvider time)
{
    public OutboundPolicyState Read()
    {
        using var client = database.CreateIsolatedClient();
        var row = client.Queryable<OutboundPolicyRecord>().InSingle(1);
        if (row is null)
        { return new(null, [], []); }
        try
        {
            var normalized = Normalize(new(
                JsonSerializer.Deserialize<OutboundNetworkGrant[]>(row.Networks) ?? throw new JsonException("出站规则为空。"),
                JsonSerializer.Deserialize<int[]>(row.LocalPorts) ?? throw new JsonException("本机端口规则为空。")));
            return new(row.Version, normalized.Networks, normalized.LocalPorts);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("持久化出站规则无效。", exception);
        }
    }

    public OutboundPolicyState Write(OutboundPolicyRequest request, string? account, string? sourceIp)
    {
        var (networks, localPorts) = Normalize(request);
        return database.InTransaction(client =>
        {
            var row = client.Queryable<OutboundPolicyRecord>().InSingle(1);
            if (row?.Version != request.ExpectedVersion)
            { throw new OutboundPolicyConflictException(); }
            var existed = row is not null;
            row ??= new OutboundPolicyRecord();
            row.Version = Guid.NewGuid().ToString("N");
            row.Networks = JsonSerializer.Serialize(networks);
            row.LocalPorts = JsonSerializer.Serialize(localPorts);
            row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (existed)
            { client.Updateable(row).ExecuteCommand(); }
            else
            { client.Insertable(row).ExecuteCommand(); }
            audit.Append(client, new AuditEntry("system.outboundPolicyChanged", AuditResults.Success, account, sourceIp,
                "outbound", JsonSerializer.Serialize(new { previousVersion = request.ExpectedVersion, version = row.Version, networks, localPorts })));
            return new OutboundPolicyState(row.Version, networks, localPorts);
        });
    }

    private static (OutboundNetworkGrant[] Networks, int[] LocalPorts) Normalize(OutboundPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Networks);
        ArgumentNullException.ThrowIfNull(request.LocalPorts);
        // JSON 管理请求沿用宿主请求体上限；规则数量仍有独立上限，防止每次 DNS 连接做无界匹配。
        if (request.Networks.Length > 1000 || request.LocalPorts.Length > 1000)
        {
            throw new ArgumentException("每类出站规则最多 1000 条。");
        }
        var networks = request.Networks.Select(rule =>
        {
            if (rule is null || !IPNetwork.TryParse(rule.Cidr, out var network))
            { throw new ArgumentException("出站 CIDR 无效。"); }
            return new OutboundNetworkGrant(network.ToString(), rule.Port);
        }).Distinct().ToArray();
        var localPorts = request.LocalPorts.Distinct().Order().ToArray();
        _ = new OutboundAddressRules(networks.Select(rule => new OutboundNetworkRule(IPNetwork.Parse(rule.Cidr), rule.Port)), localPorts);
        return (networks, localPorts);
    }

}

internal sealed record OutboundNetworkGrant(string Cidr, int Port);
internal sealed record OutboundPolicyState(string? Version, OutboundNetworkGrant[] Networks, int[] LocalPorts);
internal sealed record OutboundPolicyRequest(OutboundNetworkGrant[] Networks, int[] LocalPorts, string? ExpectedVersion = null);
internal sealed class OutboundPolicyConflictException : Exception;

[SugarTable("outbound_policy")]
internal sealed class OutboundPolicyRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "networks")]
    public string Networks { get; set; } = "[]";
    [SugarColumn(ColumnName = "local_ports")]
    public string LocalPorts { get; set; } = "[]";
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}
