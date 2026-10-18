using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.SystemInfo;

namespace TenonEdge.Host.Features.Routes;

internal sealed partial class RouteDraftStore
{
    // 所有读取均在同一草稿事务内；只带出脱离数据库的对象，随后才做拓扑/字段编译。
    internal RouteCompilationSnapshot ReadCompilation(string expectedVersion, CancellationToken token) => ReadSnapshot(expectedVersion, null, token);

    internal RouteCompilationSnapshot ReadGraphCompilation(string expectedVersion, RouteGraph graph, CancellationToken token) =>
        ReadSnapshot(expectedVersion, RouteGraphFormat.Normalize(graph, transforms, inputModules), token);

    private RouteCompilationSnapshot ReadSnapshot(string expectedVersion, RouteGraph? graph, CancellationToken token) => database.InTransaction(client =>
    {
        if (string.IsNullOrEmpty(expectedVersion) || expectedVersion.Length > 64)
        { throw new ArgumentException("须提供草稿版本。"); }
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        if (graph is null)
        { ConfigurationCredentialRequirements.RequireComplete(client); }
        var limits = RuntimeParameterStore.ReadPlanLimits(client);
        long bytes = 0;
        RouteDraft[] routes;
        if (graph is null)
        {
            if (client.Queryable<RouteDraftRecord>().Where(row => row.Enabled).Count() > limits.TotalPaths)
            { throw new RouteTopologyException(new("paths.totalLimit", "")); }
            CheckSize("route_draft", "graph", "enabled = 1", []);
            routes = client.Queryable<RouteDraftRecord>().Where(row => row.Enabled).OrderBy(row => row.Id).ToList().Select(Decode).ToArray();
        }
        else
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(graph, RouteGraphFormat.Json).Length;
            routes = [new(0, "draft-graph", "Draft graph", null, true, graph, null, 0)];
        }
        var sourceIds = new HashSet<long>();
        var connectionIds = new HashSet<long>();
        foreach (var route in routes)
        {
            token.ThrowIfCancellationRequested();
            foreach (var reference in RouteGraphFormat.References(route.Graph))
            {
                if (reference.Kind == "input")
                {
                    sourceIds.Add(reference.TargetId);
                    if (sourceIds.Count > limits.Sources)
                    { throw new RouteTopologyException(new("sources.totalLimit", route.Key, SourceId: reference.TargetId)); }
                }
                else
                {
                    connectionIds.Add(reference.TargetId);
                    if (connectionIds.Count > RouteTopologyCompiler.MaximumPaths)
                    { throw new RouteTopologyException(new("paths.totalLimit", route.Key)); }
                }
            }
        }
        var selected = sourceIds.ToArray();
        CheckSelected("input_draft", selected);
        var inputs = selected.Length == 0 ? [] : client.Queryable<InputDraftRecord>().Where(row => selected.Contains(row.Id)).ToList().Select(InputDraftStore.Decode).ToArray();
        foreach (var id in inputs.SelectMany(input => input.Module?.ConnectionIds ?? []))
        { connectionIds.Add(id); }
        var deviceIds = inputs.Select(input => input.DeviceId).Distinct().ToArray();
        CheckSelected("device_draft", deviceIds);
        var devices = deviceIds.Length == 0 ? [] : client.Queryable<DeviceDraftRecord>().Where(row => deviceIds.Contains(row.Id)).ToList().Select(DeviceDraftStore.Decode).ToArray();
        foreach (var device in devices)
        { if (device.ConnectionId is { } id) { connectionIds.Add(id); } }
        // 全部应用/编译须包含没有路线的启用输出连接；在读取内容之前仍做SQL总大小/数量检查。
        // 单图预览只读取其引用，不让无关连接影响当前图的字段/样本分析。
        if (graph is null)
        {
            if (client.Queryable<ConnectionDraftRecord>().Count() > 500)
            { throw new RouteTopologyException(new("plan.configuration", "")); }
            foreach (var id in client.Queryable<ConnectionDraftRecord>().Select(row => row.Id).ToList())
            { connectionIds.Add(id); }
        }
        var selectedConnections = connectionIds.ToArray();
        if (graph is not null && selectedConnections.Length > limits.Connections)
        { throw new RouteTopologyException(new("connections.totalLimit", "draft-graph")); }
        CheckSelected("connection_draft", selectedConnections);
        var connections = selectedConnections.Length == 0 ? [] : client.Queryable<ConnectionDraftRecord>().Where(row => selectedConnections.Contains(row.Id)).ToList().Select(ConnectionDraftStore.Decode).ToArray();
        if (graph is not null && connections.Count(connection => connection.Kind == ConnectionKind.Modbus) > limits.ModbusConnections)
        { throw new RouteTopologyException(new("connections.modbusLimit", "draft-graph")); }
        var credentials = selectedConnections.Length == 0 ? [] : client.Queryable<MqttCredentialRecord>().Where(row => selectedConnections.Contains(row.ConnectionId)).Select(row => row.ConnectionId).ToList();
        return new RouteCompilationSnapshot(expectedVersion, routes, devices.ToDictionary(device => device.Id), inputs.ToDictionary(input => input.Id),
            connections.ToDictionary(connection => connection.Id), credentials.ToHashSet(), limits, transforms, inputModules);

        void CheckSelected(string table, long[] ids)
        {
            if (ids.Length == 0)
            { return; }
            // 仅内部固定表名；引用编号经格式校验且为 long，以整数格式写入 IN，不拼接用户文本。
            var numbers = string.Join(',', ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            CheckSize(table, "content", $"id IN ({numbers})", []);
        }
        void CheckSize(string table, string column, string predicate, SugarParameter[] parameters)
        {
            token.ThrowIfCancellationRequested();
            bytes += client.Ado.GetLong($"SELECT COALESCE(SUM(length(CAST({column} AS BLOB))), 0) FROM {table} WHERE {predicate}", parameters);
            if (bytes > RuntimePlanDocument.MaximumBytes)
            { throw new RouteTopologyException(new("plan.snapshotSize", "")); }
        }
    });
}
