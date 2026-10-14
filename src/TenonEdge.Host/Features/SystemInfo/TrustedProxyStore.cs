using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>网页代理信任与部署信任取并集。不可变快照供每个请求一次捕获，不能让协议和来源使用不同版本。</summary>
internal sealed class TrustedProxyStore(ConfigDatabase database, AuditTrail audit, TrustedProxies deployment, TimeProvider time)
{
    private readonly Lock _gate = new();
    private TrustedProxySnapshot? _current;
    internal const int MaximumNetworks = 1000;

    public TrustedProxyState Read() => Current.State;
    public TrustedProxySnapshot Current
    {
        get
        {
            if (Volatile.Read(ref _current) is { } current)
            { return current; }
            lock (_gate)
            {
                if (_current is not null)
                { return _current; }
                using var client = database.CreateIsolatedClient();
                var row = client.Queryable<TrustedProxyRecord>().InSingle(1);
                try
                {
                    var values = row is null ? [] : JsonSerializer.Deserialize<string[]>(row.NetworksJson)
                        ?? throw new ArgumentException("持久代理规则为空。");
                    if (row is not null && !Guid.TryParseExact(row.Version, "N", out _))
                    { throw new ArgumentException("持久代理规则版本无效。"); }
                    var loaded = Snapshot(row?.Version, Normalize(values));
                    Volatile.Write(ref _current, loaded);
                    return loaded;
                }
                catch (Exception error) when (error is ArgumentException or JsonException)
                { throw new HostStartupException(HostExitCodes.ConfigStoreUnavailable, $"持久代理信任无效，未按空规则运行：{error.Message}"); }
            }
        }
    }

    public TrustedProxyState Write(TrustedProxyRequest request, ManagementActor actor)
    {
        if (!request.Confirmed)
        { throw new ArgumentException("须确认代理能改写请求协议和来源地址的风险。"); }
        var values = Normalize(request.Networks);
        lock (_gate)
        {
            var current = Current;
            if (request.ExpectedVersion != current.State.Version)
            { throw new TrustedProxyConflictException(); }
            var next = database.InTransaction(client =>
            {
                var account = actor.RequireAdministrator(client, time.GetUtcNow().ToUnixTimeMilliseconds()).Audit;
                var row = client.Queryable<TrustedProxyRecord>().InSingle(1);
                if (row?.Version != current.State.Version)
                { throw new TrustedProxyConflictException(); }
                if (current.State.Networks.SequenceEqual(values, StringComparer.Ordinal))
                { return current; }
                var existed = row is not null;
                row ??= new TrustedProxyRecord();
                row.Version = Guid.NewGuid().ToString("N");
                row.NetworksJson = JsonSerializer.Serialize(values);
                row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
                if (existed)
                { client.Updateable(row).ExecuteCommand(); }
                else
                { client.Insertable(row).ExecuteCommand(); }
                var updated = Snapshot(row.Version, values);
                audit.Append(client, new AuditEntry("system.trustedProxiesChanged", AuditResults.Success, account, actor.SourceIp,
                    "trustedProxies", JsonSerializer.Serialize(new { previous = current.State, current = updated.State })));
                return updated;
            });
            Volatile.Write(ref _current, next);
            return next.State;
        }
    }

    private TrustedProxySnapshot Snapshot(string? version, string[] values)
    {
        var preset = deployment.Networks.Select(network => network.ToString()).ToArray();
        return new(new(version, values, preset), TrustedProxies.Parse(values.Concat(preset)));
    }
    internal static string[] Normalize(string[]? values)
    {
        if (values is null || values.Length > MaximumNetworks || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 80))
        { throw new ArgumentException("代理规则最多1000项，每项须为非空IP地址或CIDR，最长80字符。"); }
        return TrustedProxies.Parse(values).Networks.Select(network => network.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}

internal sealed record TrustedProxyState(string? Version, string[] Networks, string[] Deployment);
internal sealed record TrustedProxySnapshot(TrustedProxyState State, TrustedProxies Proxies);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TrustedProxyRequest([property: JsonRequired] string[] Networks, [property: JsonRequired] string? ExpectedVersion, bool Confirmed);
internal sealed class TrustedProxyConflictException : Exception;
[SugarTable("trusted_proxy_rules")]
internal sealed class TrustedProxyRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)] public int Id { get; set; } = 1;
    [SugarColumn(ColumnName = "version")] public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "networks_json")] public string NetworksJson { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")] public long UpdatedAt { get; set; }
}
