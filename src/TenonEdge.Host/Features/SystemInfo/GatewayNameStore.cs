using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>只修改已有名称字段；原值比较、写入和审计同一短事务，不生成运行配置版本。</summary>
internal sealed class GatewayNameStore(ConfigDatabase database, AuditTrail audit)
{
    public GatewayNameResult Write(GatewayNameRequest request, AuditAccount account, string? sourceIp)
    {
        var name = GatewayNameRules.Normalize(request.Name);
        if (request.ExpectedName is { } expected && (expected.Length > GatewayNameRules.MaximumLength || expected.Any(char.IsControl)))
        { throw new ArgumentException("读取的原名称无效，请重新读取系统信息。"); }
        return database.InTransaction(client =>
        {
            var current = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
                ?? throw new InvalidOperationException("系统状态缺失。");
            // A value precondition is sufficient for this single display field, not a claim about other settings' versions.
            if (!string.Equals(current.GatewayName, request.ExpectedName, StringComparison.Ordinal))
            { throw new GatewayNameConflictException(); }
            if (string.Equals(current.GatewayName, name, StringComparison.Ordinal))
            { return new GatewayNameResult(name, false); }
            client.Ado.ExecuteCommand("UPDATE system_state SET gateway_name = @name WHERE id = 1", new { name });
            audit.Append(client, new AuditEntry("system.gatewayNameChanged", AuditResults.Success, account, sourceIp,
                "gatewayName", JsonSerializer.Serialize(new { previousName = current.GatewayName, name })));
            return new GatewayNameResult(name, true);
        });
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GatewayNameRequest([property: JsonRequired] string? Name, [property: JsonRequired] string? ExpectedName);
internal sealed record GatewayNameResult(string? Name, bool Changed);
internal sealed class GatewayNameConflictException : Exception;
