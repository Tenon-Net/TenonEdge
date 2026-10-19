using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Modules;

namespace TenonEdge.Runtime.Outputs;

public sealed record OutputModuleDefinition(string Kind, int Version, string DisplayName, ParameterSchema Parameters);

/// <summary>版本化输出声明、准入和原工厂视图；历史读取不运行已安装或已卸载的模块。
/// 发送/重试/隔离仍由原输出目标、注册的传输工厂和队列实现，本目录不创建后台任务。</summary>
public sealed class OutputModuleCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Outer = new(StringComparer.Ordinal)
        { "outputModule", "connectionId", "inputs", "mapping", "mappingStages", "transformStages" };
    public static OutputModuleCatalog Empty { get; } = new([]);
    private readonly ImmutableDictionary<(string Kind, int Version), IOutputModuleFactory> _modules;
    private readonly ConnectionModuleCatalog _connections;
    private readonly IModuleCredentialResolver? _credentials;
    public ImmutableArray<OutputModuleDefinition> Modules { get; }
    internal ImmutableArray<IOutputAdapterFactory> Factories { get; }

    public OutputModuleCatalog(IEnumerable<IOutputModuleFactory> modules, ConnectionModuleCatalog? connections = null, IModuleCredentialResolver? credentials = null)
    {
        _credentials = credentials;
        _connections = connections ?? ConnectionModuleCatalog.Empty;
        ArgumentNullException.ThrowIfNull(modules);
        var entries = ImmutableDictionary.CreateBuilder<(string, int), IOutputModuleFactory>();
        foreach (var module in modules)
        {
            if (module is null || !IsModuleKind(module.Kind) || module.Version < 1 ||
                string.IsNullOrWhiteSpace(module.DisplayName) || module.DisplayName.Length > 128 || module.DisplayName.Any(char.IsControl) ||
                module.Parameters is not { Scope: ParameterScope.Output } schema || schema.Parameters.Any(field => field.Type == ParameterType.Credential) ||
                !entries.TryAdd((module.Kind, module.Version), module))
            { throw new ArgumentException("输出模块标识、版本或声明无效/重复。", nameof(modules)); }
        }
        _modules = entries.ToImmutable();
        Modules = _modules.Values.OrderBy(module => module.Kind, StringComparer.Ordinal).ThenBy(module => module.Version)
            .Select(module => new OutputModuleDefinition(module.Kind, module.Version, module.DisplayName, module.Parameters)).ToImmutableArray();
        Factories = BindPlanFactories(ImmutableDictionary<string, IOutputAdapterFactory>.Empty);
    }

    internal ImmutableArray<IOutputAdapterFactory> BindPlanFactories(IReadOnlyDictionary<string, IOutputAdapterFactory> protocols) =>
        _modules.Keys.Select(key => key.Kind).Distinct(StringComparer.Ordinal).Select(kind => (IOutputAdapterFactory)new Factory(this, kind, protocols)).ToImmutableArray();

    public bool Contains(string kind, int version) => _modules.ContainsKey((kind, version));

    public RuntimeUnitPlan WithFrozenProtocol(RuntimeUnitPlan unit)
    {
        try
        {
            var (module, parameters, context) = Resolve(unit);
            var binding = CreateProtocol(module, parameters, context, unit);
            var envelope = new
            {
                version = module.Version,
                parameters,
                connections = context.Connections,
                protocol = new OutputProtocolBinding(binding.Id.Kind, binding.Configuration)
            };
            var config = unit.Configuration.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            config["outputModule"] = envelope;
            return new(unit.Id, JsonSerializer.SerializeToElement(config, Json));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    public void Validate(RuntimeUnitPlan unit)
    {
        if (!unit.Configuration.TryGetProperty("outputModule", out _) && !_modules.Keys.Any(key => key.Kind == unit.Id.Kind))
        { return; }
        try
        {
            var (module, parameters, context) = Resolve(unit);
            var expected = CreateProtocol(module, parameters, context, unit);
            var actual = ReadProtocol(unit);
            if (actual is null || expected.Id != actual.Id || expected.ConfigurationHash != actual.ConfigurationHash)
            { throw Invalid(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    public static void ValidateStoredShape(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("outputModule", out _))
        { _ = Read(unit); _ = ReadContext(unit); _ = ReadProtocol(unit) ?? throw Invalid(); }
    }

    public static ImmutableDictionary<string, ModuleConnectionSnapshot> ReadConnections(RuntimeUnitPlan unit) =>
        unit.Configuration.TryGetProperty("outputModule", out var envelope) ? ModuleConnections.Read(envelope) : ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty;

    public static OutputModuleContext ReadContext(RuntimeUnitPlan unit)
    {
        var config = unit.Configuration;
        if (!config.TryGetProperty("connectionId", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetInt64(out var id) ||
            !config.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is < 1 or > 2000 ||
            inputs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
        { throw Invalid(); }
        return new(id, inputs.EnumerateArray().Select(value => value.GetString()!), ReadConnections(unit));
    }

    public static RuntimeUnitPlan? ReadProtocol(RuntimeUnitPlan unit)
    {
        if (!unit.Configuration.TryGetProperty("outputModule", out var envelope) || !envelope.TryGetProperty("protocol", out var protocol))
        { return null; }
        if (protocol.ValueKind != JsonValueKind.Object || protocol.EnumerateObject().Count() != 2 ||
            !protocol.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !protocol.TryGetProperty("configuration", out var config))
        { throw Invalid(); }
        return CheckProtocol(unit, kind.GetString()!, config, ReadContext(unit));
    }

    private static RuntimeUnitPlan CreateProtocol(IOutputModuleFactory module, JsonElement parameters, OutputModuleContext context, RuntimeUnitPlan unit)
    {
        var binding = module.BindProtocol(parameters, context);
        if (binding is null)
        { throw Invalid(); }
        if (binding.Kind is not ("output.http" or "output.mqtt"))
        { _ = OutputTransportRoute.Read(binding.Configuration, binding.Kind, required: true); }
        return CheckProtocol(unit, binding.Kind, binding.Configuration, context);
    }

    private static RuntimeUnitPlan CheckProtocol(RuntimeUnitPlan unit, string kind, JsonElement config, OutputModuleContext context)
    {
        if (!kind.StartsWith("output.", StringComparison.Ordinal) || kind is "output.tenon" || config.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(config.GetRawText()) > 1024 * 1024 ||
            !config.TryGetProperty("connectionId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number != context.ConnectionId ||
            !context.Connections.Values.Any(connection => connection.ConnectionId == number && "output." + connection.Kind == kind) ||
            !config.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array ||
            inputs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) ||
            !inputs.EnumerateArray().Select(value => value.GetString()).SequenceEqual(context.Inputs) ||
            config.TryGetProperty("mapping", out _) || config.TryGetProperty("mappingStages", out _) || config.TryGetProperty("transformStages", out _) ||
            config.TryGetProperty("queueOnly", out var queueOnly) && queueOnly.ValueKind != JsonValueKind.False)
        { throw Invalid(); }
        _ = OutputTransportRoute.Read(config, kind);
        return new(RuntimeUnitId.Create(kind, unit.Id.Key), config);
    }

    private (IOutputModuleFactory Module, JsonElement Parameters, OutputModuleContext Context) Resolve(RuntimeUnitPlan unit)
    {
        var (version, parameters) = Read(unit);
        if (!_modules.TryGetValue((unit.Id.Kind, version), out var module))
        { throw Invalid(); }
        var context = ReadContext(unit);
        ModuleConnections.Validate(module.Parameters, parameters, context.Connections);
        foreach (var connection in context.Connections.Values.Where(connection => connection.Kind is not ("http" or "mqtt" or "modbus")))
        { _connections.Validate(connection); }
        return (module, parameters, context);
    }

    private static (int Version, JsonElement Parameters) Read(RuntimeUnitPlan unit)
    {
        var config = unit.Configuration;
        if (!IsModuleKind(unit.Id.Kind) || config.EnumerateObject().Any(property => !Outer.Contains(property.Name)) ||
            !config.TryGetProperty("outputModule", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
            envelope.EnumerateObject().Count() != 2 + (envelope.TryGetProperty("connections", out _) ? 1 : 0) + (envelope.TryGetProperty("protocol", out _) ? 1 : 0) ||
            !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1 ||
            !envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(parameters.GetRawText()) > ParameterSchema.MaximumConfigurationBytes ||
            parameters.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
        { throw Invalid(); }
        return (number, parameters);
    }

    public static bool IsModuleKind(string? kind)
    {
        if (kind is null || !kind.StartsWith("output.", StringComparison.Ordinal) || kind is "output.http" or "output.mqtt" or "output.tenon")
        { return false; }
        try
        { _ = RuntimeUnitId.Create(kind, "module/output"); return true; }
        catch (ArgumentException) { return false; }
    }

    private static ArgumentException Invalid() => new("输出模块未安装、版本不支持或参数/引用/协议无效。");

    private sealed class Factory(OutputModuleCatalog catalog, string kind, IReadOnlyDictionary<string, IOutputAdapterFactory> protocols) : IOutputAdapterFactory
    {
        public string Kind => kind;
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan unit)
        {
            try
            {
                _ = catalog.Resolve(unit);
                var protocol = ReadProtocol(unit) ?? throw Invalid();
                return Original(protocol).RecoveryGroup(new(unit.Id, protocol.Configuration));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }
        public IOutputTarget Build(RuntimeUnitPlan unit)
        {
            try
            {
                catalog.Validate(unit);
                var (_, _, context) = catalog.Resolve(unit);
                foreach (var connection in context.Connections.Values.Where(value => value.Kind is not ("http" or "mqtt" or "modbus")))
                { catalog._connections.ValidateForUse(connection, catalog._credentials); }
                var protocol = ReadProtocol(unit) ?? throw Invalid();
                var target = Original(protocol).Build(new(unit.Id, protocol.Configuration));
                if (target is null || target.Id != unit.Id || target.ConnectionId != context.ConnectionId || !target.Inputs.SequenceEqual(context.Inputs))
                { throw Invalid(); }
                return target;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }
        private IOutputAdapterFactory Original(RuntimeUnitPlan protocol) => protocols.TryGetValue(protocol.Id.Kind, out var factory) ? factory : throw Invalid();
    }
}
