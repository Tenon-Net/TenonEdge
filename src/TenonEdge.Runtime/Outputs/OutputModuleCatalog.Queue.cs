using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Outputs;

public sealed partial class OutputModuleCatalog
{
    /// <summary>冻结原传输的无来源发送单元。没有传输或未实现接续时，不将输入连接猜作输出。</summary>
    public RuntimeUnitPlan? WithQueueProtocol(RuntimeUnitId id, ModuleConnectionSnapshot connection)
    {
        try
        {
            if (id.Kind != "output." + connection.Kind || !IsModuleKind(id.Kind))
            { throw Invalid(); }
            if (!_transports.TryGetValue(id.Kind, out var factory))
            { return null; }
            _connections.Validate(connection);
            if (factory.BindQueueOnly(connection) is not { } config)
            { return null; }
            CheckQueueProtocol(id, connection.ConnectionId, config);
            return new(id, JsonSerializer.SerializeToElement(new
            {
                connectionId = connection.ConnectionId,
                inputs = Array.Empty<string>(),
                queueOutput = new { connection, protocol = new OutputProtocolBinding(id.Kind, config) },
            }, Json));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    // Runtime preparation/recovery only: recheck the frozen connection and current credential,
    // then let the same registered transport factory build the original sender's target.
    internal RuntimeUnitPlan? QueueProtocolForUse(RuntimeUnitPlan unit)
    {
        if (!unit.Configuration.TryGetProperty("queueOutput", out _))
        { return null; }
        ValidateQueue(unit);
        _connections.ValidateForUse(ReadQueueConnection(unit), _credentials);
        return ReadQueueProtocol(unit);
    }

    private void ValidateQueue(RuntimeUnitPlan unit)
    {
        try
        {
            _ = ReadQueueProtocol(unit);
            var expected = WithQueueProtocol(unit.Id, ReadQueueConnection(unit));
            if (expected is null || expected.ConfigurationHash != unit.ConfigurationHash)
            { throw Invalid(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    private static ModuleConnectionSnapshot ReadQueueConnection(RuntimeUnitPlan unit)
    {
        var config = unit.Configuration;
        if (!IsModuleKind(unit.Id.Kind) || config.ValueKind != JsonValueKind.Object || config.EnumerateObject().Count() != 3 ||
            !config.TryGetProperty("connectionId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) ||
            !config.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() != 0 ||
            !config.TryGetProperty("queueOutput", out var queue) || queue.ValueKind != JsonValueKind.Object || queue.EnumerateObject().Count() != 2 ||
            !queue.TryGetProperty("connection", out var value) || value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            !value.TryGetProperty("connectionId", out var reference) || reference.ValueKind != JsonValueKind.Number || !reference.TryGetInt64(out var referenced) || referenced != number ||
            !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || "output." + kind.GetString() != unit.Id.Kind ||
            !value.TryGetProperty("configuration", out var settings))
        { throw Invalid(); }
        var connection = new ModuleConnectionSnapshot(number, kind.GetString()!, settings);
        _ = ModuleConnectionSnapshot.ReadModuleConfiguration(connection);
        return connection;
    }

    private static RuntimeUnitPlan ReadQueueProtocol(RuntimeUnitPlan unit)
    {
        var connection = ReadQueueConnection(unit);
        var queue = unit.Configuration.GetProperty("queueOutput");
        if (!queue.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Object || protocol.EnumerateObject().Count() != 2 ||
            !protocol.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != unit.Id.Kind ||
            !protocol.TryGetProperty("configuration", out var config))
        { throw Invalid(); }
        CheckQueueProtocol(unit.Id, connection.ConnectionId, config);
        return new(unit.Id, config);
    }

    private static void CheckQueueProtocol(RuntimeUnitId id, long connectionId, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(config.GetRawText()) > 1024 * 1024 ||
            !config.TryGetProperty("connectionId", out var reference) || reference.ValueKind != JsonValueKind.Number || !reference.TryGetInt64(out var number) || number != connectionId ||
            !config.TryGetProperty("queueOnly", out var queue) || queue.ValueKind != JsonValueKind.True ||
            !config.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() != 0 ||
            config.TryGetProperty("mapping", out _) || config.TryGetProperty("mappingStages", out _) || config.TryGetProperty("transformStages", out _) ||
            config.TryGetProperty("queueOutput", out _) || config.TryGetProperty("outputModule", out _) ||
            OutputTransportRoute.Read(config, id.Kind, required: true)!.AddressTemplate.Length != 0)
        { throw Invalid(); }
    }
}
