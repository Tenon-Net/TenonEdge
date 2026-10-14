using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal enum ConfigurationImportMode { Merge, Replace }
internal sealed record ConfigurationImportPlan(ConfigurationCheckpoint Draft, ConfigurationChange[] Changes);

/// <summary>纯草稿规划：按业务key匹配/重映射，验证最终合并结果，不写库/编译/启用，不决定凭证是否可保留。</summary>
internal static class ConfigurationImportPlanner
{
    public static ConfigurationImportPlan Prepare(PortableConfiguration file, ConfigurationCheckpoint before,
        ConfigurationImportMode mode, Func<long> newId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode))
        { throw ConfigurationImportDocument.Error("import.mode", "$"); }
        ValidateKeys(file, token);
        var oldConnections = before.Connections.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var oldDevices = before.Devices.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var deviceKeys = before.Devices.ToDictionary(value => value.Id, value => value.Key);
        var oldInputs = before.Inputs.ToDictionary(value => deviceKeys[value.DeviceId] + "/" + value.Key, StringComparer.Ordinal);
        var oldRoutes = before.Routes.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var connections = mode == ConfigurationImportMode.Merge ? new(oldConnections) : new Dictionary<string, ConnectionDraft>(StringComparer.Ordinal);
        var devices = mode == ConfigurationImportMode.Merge ? new(oldDevices) : new Dictionary<string, DeviceDraft>(StringComparer.Ordinal);
        var inputs = mode == ConfigurationImportMode.Merge ? new(oldInputs) : new Dictionary<string, InputDraft>(StringComparer.Ordinal);
        var routes = mode == ConfigurationImportMode.Merge ? new(oldRoutes) : new Dictionary<string, RouteDraft>(StringComparer.Ordinal);
        // No ID supplied in the file is accepted. New IDs must come from the local
        // generator; gaps caused by a discarded preview are harmless.
        var allocated = before.Connections.Select(value => value.Id).Concat(before.Devices.Select(value => value.Id))
            .Concat(before.Inputs.Select(value => value.Id)).Concat(before.Routes.Select(value => value.Id)).ToHashSet();
        long Allocate()
        {
            var id = newId();
            if (id is < 1 or > 9007199254740991L || !allocated.Add(id))
            { throw new InvalidOperationException("本机发号器返回无效或重复编号。"); }
            return id;
        }
        for (var index = 0; index < file.Connections.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var value = file.Connections[index];
            var id = oldConnections.TryGetValue(value.ConnectionKey, out var old) ? old.Id : Allocate();
            connections[value.ConnectionKey] = Validate($"$.connections[{index}]", () => ConnectionDraftValidation.Normalize(id,
                new("import", value.ConnectionKey, value.Name, value.Kind, value.Enabled, value.Modbus, value.Mqtt, value.Http)));
        }
        var connectionsById = connections.Values.ToDictionary(value => value.Id);
        for (var index = 0; index < file.Devices.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var value = file.Devices[index];
            var path = $"$.devices[{index}]";
            var connection = value.ConnectionKey is { } key ? Connection(key, path + ".connectionKey") : null;
            var id = oldDevices.TryGetValue(value.DeviceKey, out var old) ? old.Id : Allocate();
            devices[value.DeviceKey] = Validate(path, () => DeviceDraftValidation.Normalize(id,
                new("import", value.DeviceKey, value.Name, value.Kind, value.Group, connection?.Id,
                    value.UnitId, value.AddressNotation, value.Enabled), connection));
        }
        for (var index = 0; index < file.Inputs.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var value = file.Inputs[index];
            var path = $"$.inputs[{index}]";
            if (!devices.TryGetValue(value.DeviceKey, out var device))
            { throw ConfigurationImportDocument.Error("import.referenceMissing", path + ".deviceKey"); }
            if (value.Kind != device.Kind)
            { throw ConfigurationImportDocument.Error("import.protocolMismatch", path + ".kind"); }
            var key = value.DeviceKey + "/" + value.InputKey;
            var id = oldInputs.TryGetValue(key, out var old) ? old.Id : Allocate();
            inputs[key] = Validate(path, () => InputDraftValidation.Normalize(id, device,
                new("import", value.InputKey, value.Name, value.Enabled, value.Modbus, value.Mqtt, value.Http, value.Simulator),
                device.ConnectionId is { } connectionId ? connectionsById.GetValueOrDefault(connectionId) : null));
        }
        for (var index = 0; index < file.Routes.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var value = file.Routes[index];
            var path = $"$.routes[{index}]";
            if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 64 || value.Name.Any(char.IsControl) || value.Description is { Length: > 1000 })
            { throw ConfigurationImportDocument.Error("import.schema", path); }
            var graph = value.Graph;
            if (graph?.Nodes is null || graph.Nodes.Length > RouteTopologyCompiler.MaximumNodes)
            { throw ConfigurationImportDocument.Error("import.schema", path + ".graph"); }
            var nodes = graph.Nodes.Select((node, nodeIndex) =>
            {
                var nodePath = $"{path}.graph.nodes[{nodeIndex}].config";
                if (node is null)
                { throw ConfigurationImportDocument.Error("import.schema", nodePath); }
                return node with { Config = Rewrite(node, nodePath) };
            }).ToArray();
            var normalized = Validate(path + ".graph", () => RouteGraphFormat.Normalize(new(graph.SchemaVersion, nodes, graph.Edges, graph.Layout)));
            var id = oldRoutes.TryGetValue(value.RouteKey, out var old) ? old.Id : Allocate();
            routes[value.RouteKey] = new(id, value.RouteKey, value.Name.Trim(), value.Description, value.Enabled, normalized, old?.UpdatedBy, old?.UpdatedAt ?? 0);
        }
        var candidate = new ConfigurationCheckpoint(before.Version, connections.Values.OrderBy(value => value.Id).ToArray(),
            devices.Values.OrderBy(value => value.Id).ToArray(), inputs.Values.OrderBy(value => value.Id).ToArray(),
            routes.Values.OrderBy(value => value.Id).ToArray(), before.CredentialConnections, before.Edits);
        ValidateCombined(candidate, token);
        return new(candidate, ConfigurationDiff.Between(before, candidate));

        ConnectionDraft Connection(string key, string path) => connections.TryGetValue(key, out var connection) ? connection
            : throw ConfigurationImportDocument.Error("import.referenceMissing", path);
        JsonElement Rewrite(RouteGraphNode node, string path)
        {
            if (node.Config.ValueKind != JsonValueKind.Object || node.Type is null)
            { throw ConfigurationImportDocument.Error("import.schema", path); }
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in node.Config.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (node.Type.StartsWith("input.", StringComparison.Ordinal) && property.NameEquals("sourceKeys"))
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 500)
                        { throw ConfigurationImportDocument.Error("import.schema", path + ".sourceKeys"); }
                        writer.WriteStartArray("sourceIds");
                        var sourceIndex = 0;
                        foreach (var source in property.Value.EnumerateArray())
                        {
                            var itemPath = $"{path}.sourceKeys[{sourceIndex++}]";
                            if (source.ValueKind != JsonValueKind.String || !inputs.TryGetValue(source.GetString()!, out var input))
                            { throw ConfigurationImportDocument.Error("import.referenceMissing", itemPath); }
                            writer.WriteNumberValue(input.Id);
                        }
                        writer.WriteEndArray();
                    }
                    else if (node.Type.StartsWith("output.", StringComparison.Ordinal) && property.NameEquals("connectionKey"))
                    {
                        writer.WritePropertyName("connectionId");
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        { writer.WriteNullValue(); }
                        else if (property.Value.ValueKind == JsonValueKind.String)
                        { writer.WriteNumberValue(Connection(property.Value.GetString()!, path + ".connectionKey").Id); }
                        else
                        { throw ConfigurationImportDocument.Error("import.schema", path + ".connectionKey"); }
                    }
                    else if (property.Name is "sourceIds" or "connectionId")
                    { throw ConfigurationImportDocument.Error("import.internalReference", path + "." + property.Name); }
                    else
                    { property.WriteTo(writer); }
                }
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
    }

    private static void ValidateKeys(PortableConfiguration file, CancellationToken token)
    {
        if (file.FormatVersion != 1)
        { throw ConfigurationImportDocument.Error("import.formatVersion", "$.formatVersion"); }
        if (file.Connections is null || file.Devices is null || file.Inputs is null || file.Routes is null)
        { throw ConfigurationImportDocument.Error("import.schema", "$"); }
        var issues = new ImportIssues();
        Keys(file.Connections, value => value.ConnectionKey, "connections", "connectionKey");
        Keys(file.Devices, value => value.DeviceKey, "devices", "deviceKey");
        Keys(file.Inputs, value => value.DeviceKey + "/" + value.InputKey, "inputs", "inputKey", value => BusinessKeys.IsValid(value.DeviceKey) && BusinessKeys.IsValid(value.InputKey));
        Keys(file.Routes, value => value.RouteKey, "routes", "routeKey");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var reportedFields = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < file.Inputs.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var value = file.Inputs[index];
            if (value is null)
            { continue; }
            Fields(value.Modbus?.Points, point => point.Key, "modbus.points");
            Fields(value.Mqtt?.Fields, field => field.Key, "mqtt.fields");
            Fields(value.Http?.Fields, field => field.Key, "http.fields");
            void Fields<T>(T[]? items, Func<T, string> keyOf, string group) where T : class
            {
                if (items is null)
                { return; }
                for (var fieldIndex = 0; fieldIndex < items.Length; fieldIndex++)
                {
                    var path = $"$.inputs[{index}].{group}[{fieldIndex}].key";
                    if (items[fieldIndex] is not { } item)
                    { issues.Add("import.schema", path); continue; }
                    var key = keyOf(item);
                    if (!BusinessKeys.IsValid(key))
                    { issues.Add("import.key", path); continue; }
                    var identity = value.DeviceKey + "/" + key;
                    if (!fields.TryAdd(identity, path))
                    {
                        if (reportedFields.Add(identity))
                        { issues.Add("import.duplicateFieldKey", fields[identity]); }
                        issues.Add("import.duplicateFieldKey", path);
                    }
                }
            }
        }
        for (var index = 0; index < file.Connections.Length; index++)
        {
            var value = file.Connections[index];
            if (value?.Credential is not { } credential)
            { continue; }
            if (value.Kind == ConnectionKind.Modbus || credential.Status != "requiresInput" || credential.AccountIdentity is not { Length: 71 } identity ||
                !identity.StartsWith("sha256:", StringComparison.Ordinal) || identity[7..].Any(character => !char.IsAsciiHexDigitLower(character)))
            { issues.Add("import.credentialHint", $"$.connections[{index}].credential"); }
        }
        issues.ThrowIfAny();

        void Keys<T>(T[] values, Func<T, string> keyOf, string collection, string property, Func<T, bool>? valid = null) where T : class
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var value = values[index];
                var path = $"$.{collection}[{index}].{property}";
                if (value is null)
                { issues.Add("import.schema", path); continue; }
                var key = keyOf(value);
                if (valid is null ? !BusinessKeys.IsValid(key) : !valid(value))
                { issues.Add("import.key", path); continue; }
                if (!seen.TryAdd(key, index))
                {
                    if (reported.Add(key))
                    { issues.Add("import.duplicateKey", $"$.{collection}[{seen[key]}].{property}"); }
                    issues.Add("import.duplicateKey", path);
                }
            }
        }
    }

    private static T Validate<T>(string path, Func<T> operation)
    {
        try
        { return operation(); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        { throw ConfigurationImportDocument.Error("import.schema", path); }
    }

    private static void ValidateCombined(ConfigurationCheckpoint draft, CancellationToken token)
    {
        var connections = draft.Connections.ToDictionary(value => value.Id);
        var devices = draft.Devices.ToDictionary(value => value.Id);
        var inputs = draft.Inputs.ToDictionary(value => value.Id);
        var fields = new Dictionary<long, HashSet<string>>();
        foreach (var device in draft.Devices)
        {
            token.ThrowIfCancellationRequested();
            Validate("$.devices", () => DeviceDraftValidation.Normalize(device.Id,
                new("import", device.Key, device.Name, device.Kind, device.Group, device.ConnectionId, device.UnitId, device.AddressNotation, device.Enabled),
                device.ConnectionId is { } id ? connections.GetValueOrDefault(id) : null));
        }
        foreach (var input in draft.Inputs)
        {
            token.ThrowIfCancellationRequested();
            if (!devices.TryGetValue(input.DeviceId, out var device))
            { throw ConfigurationImportDocument.Error("import.referenceMissing", "$.inputs"); }
            if (device.Kind != input.Kind)
            { throw ConfigurationImportDocument.Error("import.protocolMismatch", "$.inputs"); }
            Validate("$.inputs", () => InputDraftValidation.Normalize(input.Id, device,
                new("import", input.Key, input.Name, input.Enabled, input.Modbus, input.Mqtt, input.Http, input.Simulator),
                device.ConnectionId is { } id ? connections.GetValueOrDefault(id) : null));
            if (!fields.TryGetValue(input.DeviceId, out var keys))
            { fields.Add(input.DeviceId, keys = new(StringComparer.Ordinal)); }
            foreach (var key in InputDraftValidation.FieldKeys(input))
            { if (!keys.Add(key)) { throw ConfigurationImportDocument.Error("import.duplicateFieldKey", "$.inputs"); } }
        }
        foreach (var route in draft.Routes)
        {
            token.ThrowIfCancellationRequested();
            var graph = Validate("$.routes", () => RouteGraphFormat.Normalize(route.Graph));
            var nodeIds = graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            if (graph.Edges.Any(edge => !nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target)))
            { throw ConfigurationImportDocument.Error("import.referenceMissing", "$.routes"); }
            foreach (var reference in RouteGraphFormat.References(graph))
            {
                if (reference.Kind == "input" ? !inputs.ContainsKey(reference.TargetId) : !connections.ContainsKey(reference.TargetId))
                { throw ConfigurationImportDocument.Error("import.referenceMissing", "$.routes"); }
            }
            foreach (var node in graph.Nodes)
            {
                // Tenon output is not implemented in this T3 Host. A recognized
                // future graph kind must not be mistaken for an installed module.
                if (node.Type == "output.tenon")
                { throw ConfigurationImportDocument.Error("import.moduleUnavailable", "$.routes"); }
                if (node.Type.StartsWith("input.", StringComparison.Ordinal))
                {
                    foreach (var id in node.Config.GetProperty("sourceIds").EnumerateArray())
                    {
                        if (node.Type != "input." + inputs[id.GetInt64()].Kind.ToString().ToLowerInvariant())
                        { throw ConfigurationImportDocument.Error("import.protocolMismatch", "$.routes"); }
                    }
                }
                else if (node.Type.StartsWith("output.", StringComparison.Ordinal) && node.Config.TryGetProperty("connectionId", out var id) && id.ValueKind != JsonValueKind.Null &&
                    node.Type != "output." + connections[id.GetInt64()].Kind.ToString().ToLowerInvariant())
                { throw ConfigurationImportDocument.Error("import.protocolMismatch", "$.routes"); }
            }
        }
    }
}
