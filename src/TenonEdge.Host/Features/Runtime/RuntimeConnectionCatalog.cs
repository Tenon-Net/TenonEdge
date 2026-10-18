using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record ConnectionListDefinition(string? Key, string Name, string Kind, string Target, bool Enabled);
internal sealed record ConnectionListUser(string Kind, string Key, string Name, bool Enabled, bool Active);

/// <summary>只读取结构化的非秘密配置与冻结协议委托；不执行扩展 Describe/Build 或读取凭证。
/// 正式使用者与仅补发队列的单元分开，停用/未接线的配置引用仍可核对。</summary>
internal sealed class RuntimeConnectionCatalog
{
    internal sealed class Entry(ConnectionListDefinition definition)
    {
        internal ConnectionListDefinition Definition { get; } = definition;
        internal HashSet<(string Protocol, string Identity)> Identities { get; } = [];
        internal List<RuntimeUnitPlan> Units { get; } = [];
        internal List<ConnectionListUser> Users { get; } = [];
    }

    internal static Dictionary<long, Entry> Read(RuntimePlanDocument? plan, ConfigurationCheckpoint? checkpoint, string nodeId)
    {
        var entries = checkpoint?.Connections.ToDictionary(connection => connection.Id, connection => new Entry(Describe(connection))) ?? [];
        var units = plan?.Units ?? [];
        var diagnostic = plan?.ToPlan(1).DiagnosticObjects;
        var activeIds = units.Select(unit => (unit.Id.Kind, unit.Id.Key))
            .Concat(diagnostic?.Values.Select(id => (id.Kind, id.Key)) ?? []).ToHashSet();
        foreach (var unit in units)
        {
            foreach (var settings in RuntimeConnectionReferences.ReadHttp(unit))
            {
                Add(settings.ConnectionId, "http", settings.BaseAddress,
                    JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs }), unit);
            }
            foreach (var settings in RuntimeConnectionReferences.ReadModbus(unit))
            { Add(settings.ConnectionId, "modbus", HostPort(settings.Host, settings.Port), settings.Identity, unit); }
            foreach (var settings in RuntimeConnectionReferences.ReadMqtt(unit))
            {
                var clientId = settings.ClientId ?? $"tenon-{Guid.Parse(nodeId):N}-{settings.ConnectionId}";
                Add(settings.ConnectionId, "mqtt", settings.Recipient, settings.ConnectionIdentity(clientId), unit);
            }
            foreach (var connection in RuntimeConnectionReferences.ReadCustom(unit))
            {
                // Generic parameters are not a target address. Do not guess or expose the full configuration here.
                Add(connection.ConnectionId, connection.Kind, "扩展连接（目标由适配器解析）",
                    ModuleConnectionSnapshot.ModuleConfigurationIdentity(connection), unit);
            }
        }
        if (entries.Values.Any(entry => entry.Identities.Count > 1))
        { throw new RuntimePlanDocumentException("同一连接的冻结协议配置不一致。"); }
        if (checkpoint is not null)
        {
            foreach (var (id, users) in ReadUsers(checkpoint, IsActive))
            {
                if (entries.TryGetValue(id, out var entry))
                { entry.Users.AddRange(users); }
            }
        }
        else
        {
            foreach (var entry in entries.Values)
            {
                entry.Users.AddRange(entry.Units.Where(unit => !QueueOnly(unit)).Select(unit =>
                    new ConnectionListUser(unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal) ? "input" : "output",
                        unit.Id.Key, unit.Id.Key, true, true)).Distinct());
            }
        }
        return entries;

        bool IsActive(string kind, string key) => activeIds.Contains((kind, key));

        void Add(long id, string kind, string target, string identity, RuntimeUnitPlan unit)
        {
            if (!entries.TryGetValue(id, out var entry))
            {
                entry = new(new(null, id.ToString(CultureInfo.InvariantCulture), kind, target, true));
                entries.Add(id, entry);
            }
            entry.Identities.Add((kind, identity));
            if (!entry.Units.Any(value => value.Id == unit.Id))
            { entry.Units.Add(unit); }
        }
    }

    internal static Dictionary<long, ConnectionListUser[]> ReadUsers(ConfigurationCheckpoint checkpoint, Func<string, string, bool>? active = null)
    {
        var users = new List<(long Id, ConnectionListUser User)>();
        var devices = checkpoint.Devices.ToDictionary(device => device.Id);
        foreach (var source in checkpoint.Inputs)
        {
            if (!devices.TryGetValue(source.DeviceId, out var device))
            { continue; }
            var key = device.Key + "/" + source.Key;
            var kind = source.Module?.Kind ?? "input." + source.Kind.ToString().ToLowerInvariant();
            var user = new ConnectionListUser("input", key, device.Name + " / " + source.Name,
                device.Enabled && source.Enabled, active?.Invoke(kind, key) == true);
            var ids = source.Module?.ConnectionIds ?? (device.ConnectionId is { } connectionId ? [connectionId] : Enumerable.Empty<long>());
            foreach (var id in ids.Distinct())
            { users.Add((id, user)); }
        }
        foreach (var route in checkpoint.Routes)
        {
            foreach (var node in route.Graph.Nodes.Where(node => node.Type.StartsWith("output.", StringComparison.Ordinal)))
            {
                var key = route.Key + "/" + node.Id;
                var user = new ConnectionListUser("output", key, route.Name + " / " + node.Name,
                    route.Enabled, active?.Invoke(node.Type, key) == true);
                foreach (var reference in RouteGraphFormat.References(route.Graph with { Nodes = [node] }))
                { users.Add((reference.TargetId, user)); }
            }
        }
        return users.GroupBy(item => item.Id).ToDictionary(group => group.Key,
            group => group.Select(item => item.User).Distinct().OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }

    internal static ConnectionListDefinition Describe(ConnectionDraft connection) => new(connection.Key, connection.Name,
        connection.Kind.ToString().ToLowerInvariant(), connection.Kind switch
        {
            ConnectionKind.Modbus => HostPort(connection.Modbus!.Host, connection.Modbus.Port),
            ConnectionKind.Mqtt => (connection.Mqtt!.Tls ? "mqtts://" : "mqtt://") + HostPort(connection.Mqtt.Host, connection.Mqtt.Port),
            ConnectionKind.Http => connection.Http!.BaseAddress,
            _ => throw new RuntimePlanDocumentException("连接协议不可识别。")
        }, connection.Enabled);

    internal static bool DraftChanged(ConnectionDraft? effective, ConnectionDraft? draft) =>
        !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(effective), JsonSerializer.SerializeToElement(draft));

    internal static bool QueueOnly(RuntimeUnitPlan unit) => RuntimeConnectionReferences.Protocol(unit).Configuration.TryGetProperty("queueOnly", out var value) && value.ValueKind == JsonValueKind.True;
    private static string HostPort(string host, int port) => (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);
}
