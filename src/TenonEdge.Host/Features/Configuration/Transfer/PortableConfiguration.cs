using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Host.Features.Configuration.Transfer;

/// <summary>GW-16 文件契约：仅业务标识与非秘密配置，不复用数据库行、版本快照或运行计划。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableConfiguration(int FormatVersion, PortableConnection[] Connections, PortableDevice[] Devices,
    PortableInput[] Inputs, PortableRoute[] Routes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableConnection(string ConnectionKey, string Name, ConnectionKind Kind, bool Enabled,
    ModbusConnectionDraft? Modbus, MqttConnectionDraft? Mqtt, HttpConnectionDraft? Http, PortableCredentialRequirement? Credential);

/// <summary>非秘密账户比较摘要，不是凭证、内部对象ID或可用于认证的值。跨网关缺少凭证时仍须重新填写。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableCredentialRequirement(string Status, string AccountIdentity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableDevice(string DeviceKey, string Name, DeviceKind Kind, string? Group, string? ConnectionKey,
    int? UnitId, ModbusAddressNotation AddressNotation, bool Enabled, string? ModuleKind = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableInput(string DeviceKey, string InputKey, string Name, DeviceKind Kind, bool Enabled,
    ModbusSourceDraft? Modbus, MqttSourceDraft? Mqtt, HttpSourceDraft? Http, SimulatorSourceDraft? Simulator, PortableModuleSource? Module = null);

// Scalar parameters remain verbatim; connection references are explicit business keys,
// so exporting an uninstalled module never has to guess which scalar is an internal ID.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableModuleSource(string Kind, int Version, JsonElement Parameters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? Connections = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableRoute(string RouteKey, string Name, string? Description, bool Enabled, PortableGraph Graph);

// Route-local node IDs/layout are portable; source and connection IDs inside config are not.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PortableGraph(int SchemaVersion, RouteGraphNode[] Nodes, RouteGraphEdge[] Edges, RouteGraphLayout Layout);

internal sealed class ConfigurationTransferException(string code, string path) : Exception(code)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

internal static class ConfigurationExport
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static PortableConfiguration Create(ConfigurationCheckpoint draft, IReadOnlyDictionary<long, string> credentialIdentities,
        CancellationToken token)
    {
        var connections = draft.Connections.Select(value => ConnectionDraftValidation.Normalize(value.Id,
            new("export", value.Key, value.Name, value.Kind, value.Enabled, value.Modbus, value.Mqtt, value.Http))).ToDictionary(value => value.Id);
        var devices = draft.Devices.ToDictionary(value => value.Id);
        var sources = draft.Inputs.ToDictionary(value => value.Id, value => Device(value.DeviceId).Key + "/" + value.Key);
        var exportedConnections = connections.Values.Select(value => new PortableConnection(value.Key, value.Name, value.Kind,
            value.Enabled, value.Modbus, value.Mqtt, value.Http, credentialIdentities.TryGetValue(value.Id, out var identity)
                ? new("requiresInput", identity) : null)).ToArray();
        var exportedDevices = draft.Devices.Select(value =>
        {
            token.ThrowIfCancellationRequested();
            var connection = value.ConnectionId is { } id ? Connection(id) : null;
            var normalized = DeviceDraftValidation.Normalize(value.Id, new("export", value.Key, value.Name, value.Kind,
                value.Group, value.ConnectionId, value.UnitId, value.AddressNotation, value.Enabled, value.ModuleKind), connection);
            return new PortableDevice(normalized.Key, normalized.Name, normalized.Kind, normalized.Group, connection?.Key,
                normalized.UnitId, normalized.AddressNotation, normalized.Enabled, normalized.ModuleKind);
        }).ToArray();
        var exportedInputs = draft.Inputs.Select(value =>
        {
            token.ThrowIfCancellationRequested();
            var device = Device(value.DeviceId);
            var normalized = InputDraftValidation.Normalize(value.Id, device, new("export", value.Key, value.Name,
                value.Enabled, value.Modbus, value.Mqtt, value.Http, value.Simulator, value.Module), device.ConnectionId is { } id ? Connection(id) : null);
            return new PortableInput(device.Key, normalized.Key, normalized.Name, normalized.Kind, normalized.Enabled,
                normalized.Modbus, normalized.Mqtt, normalized.Http, normalized.Simulator, normalized.Module is { } module
                    ? new(module.Kind, module.Version, module.Parameters, module.Connections?.ToDictionary(pair => pair.Key,
                        pair => connections.TryGetValue(pair.Value, out var connection) ? connection.Key
                            : throw new ConfigurationTransferException("configuration.exportReferenceMissing", "$.inputs.module.connections"), StringComparer.Ordinal)) : null);
        }).ToArray();
        var exportedRoutes = draft.Routes.Select((route, index) =>
        {
            token.ThrowIfCancellationRequested();
            var graph = RouteGraphFormat.NormalizeStored(route.Graph);
            var nodes = graph.Nodes.Select((node, nodeIndex) => node with
            {
                Config = Rewrite(node, $"$.routes[{index}].graph.nodes[{nodeIndex}].config")
            }).ToArray();
            return new PortableRoute(route.Key, route.Name, route.Description, route.Enabled, new(graph.SchemaVersion, nodes, graph.Edges, graph.Layout));
        }).ToArray();
        return new(1, exportedConnections, exportedDevices, exportedInputs, exportedRoutes);

        ConnectionDraft Connection(long id) => connections.TryGetValue(id, out var value) ? value
            : throw new ConfigurationTransferException("configuration.exportReferenceMissing", "$.devices.connectionKey");
        DeviceDraft Device(long id) => devices.TryGetValue(id, out var value) ? value
            : throw new ConfigurationTransferException("configuration.exportReferenceMissing", "$.inputs.deviceKey");
        JsonElement Rewrite(RouteGraphNode node, string path)
        {
            // Re-emit only the resource-reference properties. Scalar JSON lexemes,
            // custom payload constants and route-local node IDs remain untouched.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in node.Config.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (node.Type.StartsWith("input.", StringComparison.Ordinal) && property.NameEquals("sourceIds"))
                    {
                        writer.WriteStartArray("sourceKeys");
                        foreach (var id in property.Value.EnumerateArray())
                        {
                            if (!sources.TryGetValue(id.GetInt64(), out var key))
                            { throw new ConfigurationTransferException("configuration.exportReferenceMissing", path + ".sourceKeys"); }
                            writer.WriteStringValue(key);
                        }
                        writer.WriteEndArray();
                    }
                    else if (OutputModuleCatalog.IsModuleKind(node.Type) && property.NameEquals("connections"))
                    {
                        writer.WritePropertyName("connectionKeys");
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        { writer.WriteNullValue(); }
                        else
                        {
                            writer.WriteStartObject();
                            foreach (var reference in property.Value.EnumerateObject())
                            {
                                if (!connections.TryGetValue(reference.Value.GetInt64(), out var connection))
                                { throw new ConfigurationTransferException("configuration.exportReferenceMissing", path + ".connectionKeys"); }
                                writer.WriteString(reference.Name, connection.Key);
                            }
                            writer.WriteEndObject();
                        }
                    }
                    else if (node.Type.StartsWith("output.", StringComparison.Ordinal) && property.NameEquals("connectionId"))
                    {
                        writer.WritePropertyName("connectionKey");
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        { writer.WriteNullValue(); }
                        else if (connections.TryGetValue(property.Value.GetInt64(), out var connection))
                        { writer.WriteStringValue(connection.Key); }
                        else
                        { throw new ConfigurationTransferException("configuration.exportReferenceMissing", path + ".connectionKey"); }
                    }
                    else
                    { property.WriteTo(writer); }
                }
                writer.WriteEndObject();
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            return json.RootElement.Clone();
        }
    }

    public static byte[] Serialize(PortableConfiguration value, CancellationToken token, int maximumBytes = MaximumBytes)
    {
        token.ThrowIfCancellationRequested();
        using var buffer = new BoundedExportStream(maximumBytes);
        JsonSerializer.Serialize(buffer, value, Json);
        token.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    // Bound the serialized body while writing, including growth from resource IDs
    // to business keys. Never allocate an arbitrarily oversized file then truncate it.
    private sealed class BoundedExportStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (count > maximum - Length)
            { throw new ConfigurationTransferException("configuration.exportTooLarge", "$"); }
        }
    }
}
