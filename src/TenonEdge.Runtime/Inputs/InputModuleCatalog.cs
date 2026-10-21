using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Runtime.Inputs;

public sealed record InputModuleDefinition(string Kind, int Version, string DisplayName, ParameterSchema Parameters);

/// <summary>不可变的编译期输入目录。参数/版本准入、字段描述和原运行时构建共用；历史读取不执行模块。
/// 与内置输入配置分开的 inputModule 封套，不改写旧快照或其哈希。</summary>
public sealed class InputModuleCatalog
{
    public static InputModuleCatalog Empty { get; } = new([]);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly ImmutableDictionary<(string Kind, int Version), IInputModuleFactory> _modules;
    private readonly ConnectionModuleCatalog _connections;
    private readonly IModuleCredentialResolver? _credentials;
    public ImmutableArray<InputModuleDefinition> Modules { get; }
    internal ImmutableArray<IInputUnitFactory> Factories { get; }

    internal ImmutableArray<IInputUnitFactory> BindPlanFactories(IReadOnlyDictionary<string, IInputUnitFactory> adapters) =>
        _modules.Values.GroupBy(module => module.Kind, StringComparer.Ordinal)
            .Select(group => (IInputUnitFactory)new Factory(this, group.Key, group.First().UsesDurableHandoff, adapters)).ToImmutableArray();

    public InputModuleCatalog(IEnumerable<IInputModuleFactory> modules, ConnectionModuleCatalog? connections = null, IModuleCredentialResolver? credentials = null)
    {
        _credentials = credentials;
        _connections = connections ?? ConnectionModuleCatalog.Empty;
        ArgumentNullException.ThrowIfNull(modules);
        var entries = ImmutableDictionary.CreateBuilder<(string, int), IInputModuleFactory>();
        foreach (var module in modules)
        {
            if (module is null || !IsModuleKind(module.Kind) ||
                module.Version < 1 || string.IsNullOrWhiteSpace(module.DisplayName) || module.DisplayName.Length > 128 || module.DisplayName.Any(char.IsControl) ||
                module.Parameters is not { Scope: ParameterScope.Input } schema ||
                schema.Parameters.Any(field => field.Type is ParameterType.Credential) ||
                !entries.TryAdd((module.Kind, module.Version), module))
            { throw new ArgumentException("输入模块标识、版本、参数声明无效或重复。", nameof(modules)); }
        }
        _modules = entries.ToImmutable();
        var groups = _modules.Values.GroupBy(module => module.Kind, StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Select(module => module.UsesDurableHandoff).Distinct().Count() != 1))
        { throw new ArgumentException("同一输入模块各版本必须使用相同接管约定。", nameof(modules)); }
        Modules = _modules.Values.OrderBy(module => module.Kind, StringComparer.Ordinal).ThenBy(module => module.Version)
            .Select(module => new InputModuleDefinition(module.Kind, module.Version, module.DisplayName, module.Parameters)).ToImmutableArray();
        Factories = groups.Select(group => (IInputUnitFactory)new Factory(this, group.Key, group.First().UsesDurableHandoff)).ToImmutableArray();
    }

    public ImmutableArray<InputModuleField> Describe(RuntimeUnitPlan unit)
    {
        try
        {
            var (module, parameters) = Resolve(unit);
            var fields = module.Describe(parameters);
            ValidateFields(fields);
            var frozen = ReadFrozenFields(unit);
            if (frozen is { } expected && !expected.OrderBy(field => field.Key, StringComparer.Ordinal).SequenceEqual(fields.OrderBy(field => field.Key, StringComparer.Ordinal)))
            { throw Invalid(); }
            return fields;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw Invalid(); }
    }

    /// <summary>编译时冻结非秘密字段，供重启后的只读诊断使用；不构建输入或调用网络。</summary>
    public RuntimeUnitPlan WithFrozenFields(RuntimeUnitPlan unit)
    {
        var fields = Describe(unit);
        var (version, parameters) = Read(unit);
        var envelope = new Dictionary<string, object?>
        {
            ["version"] = version,
            ["parameters"] = parameters,
            ["fields"] = fields.OrderBy(field => field.Key, StringComparer.Ordinal).ToArray()
        };
        if (unit.Configuration.GetProperty("inputModule").TryGetProperty("connections", out var connections))
        { envelope["connections"] = connections; }
        var (module, _) = Resolve(unit);
        var binding = CreateProtocol(module, parameters, unit);
        if (binding is not null)
        { envelope["protocol"] = new InputProtocolBinding(binding.Id.Kind, binding.Configuration); }
        return new(unit.Id, JsonSerializer.SerializeToElement(new
        {
            inputModule = envelope,
        }, Json));
    }

    /// <summary>不依赖安装目录、不执行模块；旧种子没有字段描述时返回 null，不从当前草稿回填。</summary>
    public static ImmutableArray<InputModuleField>? ReadFrozenFields(RuntimeUnitPlan unit)
    {
        var envelope = unit.Configuration.GetProperty("inputModule");
        if (!envelope.TryGetProperty("fields", out var value))
        { return null; }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1000)
        { throw Invalid(); }
        try
        {
            var fields = value.Deserialize<ImmutableArray<InputModuleField>>(Json);
            ValidateFields(fields);
            return fields;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void ValidateFields(ImmutableArray<InputModuleField> fields)
    {
        if (fields.IsDefault || fields.Length > 1000 || fields.Any(field => field is null || !BusinessKeys.IsValid(field.Key) ||
            !Enum.IsDefined(field.Type) || field.Unit?.Length > 64 || field.Unit?.Any(char.IsControl) == true) ||
            fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length)
        { throw Invalid(); }
    }

    /// <summary>已安装模块必须使用明确封套；带封套但模块缺失同样拒绝。旧内置/测试工厂的原配置不变。</summary>
    public void Validate(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("inputModule", out _) || _modules.Keys.Any(key => key.Kind == unit.Id.Kind))
        {
            _ = Describe(unit);
            var (module, parameters) = Resolve(unit);
            var expected = CreateProtocol(module, parameters, unit);
            var frozen = ReadProtocol(unit);
            if (expected?.Id != frozen?.Id || expected?.ConfigurationHash != frozen?.ConfigurationHash)
            { throw Invalid(); }
        }
    }

    private static RuntimeUnitPlan? CreateProtocol(IInputModuleFactory module, JsonElement parameters, RuntimeUnitPlan unit)
    {
        try
        {
            var binding = module.BindProtocol(parameters, ReadConnections(unit));
            if (binding is null && ModuleConnectionReferences.Flatten(ReadConnections(unit).Values).Any(connection => connection.Kind is "mqtt" or "modbus"))
            { throw Invalid(); }
            return binding is null ? null : CheckProtocol(unit, binding.Kind, binding.Configuration);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    /// <summary>只读取已冻结的原协议委托，不执行模块；用于历史校验及协议级保护。</summary>
    public static RuntimeUnitPlan? ReadProtocol(RuntimeUnitPlan unit)
    {
        if (!unit.Configuration.TryGetProperty("inputModule", out var envelope) || !envelope.TryGetProperty("protocol", out var value))
        { return null; }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("configuration", out var config))
        { throw Invalid(); }
        return CheckProtocol(unit, kind.GetString()!, config);
    }

    private static RuntimeUnitPlan CheckProtocol(RuntimeUnitPlan unit, string kind, JsonElement config)
    {
        if (kind is not ("input.http" or "input.mqtt" or "input.modbus") || config.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(config.GetRawText()) > 1024 * 1024 ||
            !config.TryGetProperty("connectionId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) ||
            !ModuleConnectionReferences.Flatten(ReadConnections(unit).Values).Any(connection => connection.ConnectionId == number && "input." + connection.Kind == kind))
        { throw Invalid(); }
        return new(RuntimeUnitId.Create(kind, unit.Id.Key), config);
    }

    public static void ValidateStoredShape(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("inputModule", out _))
        { _ = Read(unit); }
    }

    private (IInputModuleFactory Module, JsonElement Parameters) Resolve(RuntimeUnitPlan unit)
    {
        var (version, parameters) = Read(unit);
        if (!_modules.TryGetValue((unit.Id.Kind, version), out var module))
        { throw Invalid(); }
        TenonEdge.Runtime.Modules.ModuleConnections.Validate(module.Parameters, parameters, ReadConnections(unit));
        foreach (var connection in ReadConnections(unit).Values.Where(connection => connection.Kind is not ("http" or "mqtt" or "modbus")))
        { _connections.Validate(connection); }
        return (module, parameters);
    }

    /// <summary>历史和运行时都可结构读取，不执行模块，也不读取当前草稿。具体协议校验属于宿主。</summary>
    public static ImmutableDictionary<string, ModuleConnectionSnapshot> ReadConnections(RuntimeUnitPlan unit)
    {
        if (!unit.Configuration.TryGetProperty("inputModule", out var envelope))
        { return ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty; }
        return TenonEdge.Runtime.Modules.ModuleConnections.Read(envelope);
    }

    private static (int Version, JsonElement Parameters) Read(RuntimeUnitPlan unit)
    {
        var config = unit.Configuration;
        if (!ValidKind(unit.Id.Kind) || unit.Id.KeySegments.Count != 2 || config.EnumerateObject().Count() != 1 ||
            !config.TryGetProperty("inputModule", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
            envelope.EnumerateObject().Count() != 2 + (envelope.TryGetProperty("fields", out _) ? 1 : 0) + (envelope.TryGetProperty("connections", out _) ? 1 : 0) + (envelope.TryGetProperty("protocol", out _) ? 1 : 0) ||
            !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1 ||
            !envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(parameters.GetRawText()) > ParameterSchema.MaximumConfigurationBytes ||
            parameters.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
        { throw Invalid(); }
        _ = ReadFrozenFields(unit);
        _ = ReadConnections(unit);
        _ = ReadProtocol(unit);
        return (number, parameters);
    }

    public bool Contains(string kind, int version) => _modules.ContainsKey((kind, version));

    public static bool IsModuleKind(string? kind) => ValidKind(kind) && kind is not ("input.simulator" or "input.http" or "input.mqtt" or "input.modbus");

    public static bool ValidKind(string? kind)
    {
        if (kind is null || !kind.StartsWith("input.", StringComparison.Ordinal) || kind.Length <= 6)
        { return false; }
        try
        { _ = RuntimeUnitId.Create(kind, "module/input"); return true; }
        catch (ArgumentException)
        { return false; }
    }
    private static ArgumentException Invalid() => new("输入模块未安装、版本不支持或参数/字段无效。");

    private sealed class Factory(InputModuleCatalog catalog, string kind, bool handoff,
        IReadOnlyDictionary<string, IInputUnitFactory>? adapters = null) : IInputUnitFactory
    {
        public string Kind => kind;
        public bool UsesDurableHandoff => handoff;
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit)
        {
            try
            {
                var (module, parameters) = catalog.Resolve(unit);
                if (ReadProtocol(unit) is { } protocol)
                { return ProtocolFactory(protocol).RecoveryGroup(new(unit.Id, protocol.Configuration)); }
                return module.RecoveryGroup(new(unit.Id, parameters)) ?? throw Invalid();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            try
            {
                catalog.Validate(unit);
                var (module, parameters) = catalog.Resolve(unit);
                foreach (var connection in ReadConnections(unit).Values.Where(value => value.Kind is not ("http" or "mqtt" or "modbus")))
                { catalog._connections.ValidateForUse(connection, catalog._credentials); }
                if (ReadProtocol(unit) is { } protocol)
                { return ProtocolFactory(protocol).Build(new(unit.Id, protocol.Configuration), context); }
                return module.Build(new(unit.Id, parameters), context) ?? throw Invalid();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }

        private IInputUnitFactory ProtocolFactory(RuntimeUnitPlan protocol)
        {
            if (adapters is null || !adapters.TryGetValue(protocol.Id.Kind, out var factory) || factory.UsesDurableHandoff != handoff)
            { throw Invalid(); }
            return factory;
        }
    }
}
