using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Runtime.Inputs;

public sealed record InputModuleDefinition(string Kind, int Version, string DisplayName, ParameterSchema Parameters);

/// <summary>不可变的编译期输入目录。参数/版本准入、字段描述和原运行时构建共用；历史读取不执行模块。
/// 与内置输入配置分开的 inputModule 封套，不改写旧快照或其哈希。</summary>
public sealed class InputModuleCatalog
{
    public static InputModuleCatalog Empty { get; } = new([]);
    private readonly ImmutableDictionary<(string Kind, int Version), IInputModuleFactory> _modules;
    public ImmutableArray<InputModuleDefinition> Modules { get; }
    internal ImmutableArray<IInputUnitFactory> Factories { get; }

    public InputModuleCatalog(IEnumerable<IInputModuleFactory> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var entries = ImmutableDictionary.CreateBuilder<(string, int), IInputModuleFactory>();
        foreach (var module in modules)
        {
            if (module is null || !ValidKind(module.Kind) || module.Kind is "input.simulator" or "input.http" or "input.mqtt" or "input.modbus" ||
                module.Version < 1 || string.IsNullOrWhiteSpace(module.DisplayName) || module.DisplayName.Length > 128 || module.DisplayName.Any(char.IsControl) ||
                module.Parameters is not { Scope: ParameterScope.Input } schema ||
                schema.Parameters.Any(field => field.Type is ParameterType.Credential or ParameterType.ConnectionReference) ||
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
            if (fields.IsDefault || fields.Length > 1000 || fields.Any(field => field is null || !BusinessKeys.IsValid(field.Key) ||
                !Enum.IsDefined(field.Type) || field.Unit?.Length > 64 || field.Unit?.Any(char.IsControl) == true) ||
                fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            { throw new ArgumentException("输入字段描述无效。"); }
            return fields;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw Invalid(); }
    }

    /// <summary>已安装模块必须使用明确封套；带封套但模块缺失同样拒绝。旧内置/测试工厂的原配置不变。</summary>
    public void Validate(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("inputModule", out _) || _modules.Keys.Any(key => key.Kind == unit.Id.Kind))
        { _ = Describe(unit); }
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
        module.Parameters.ThrowIfInvalid(parameters);
        return (module, parameters);
    }

    private static (int Version, JsonElement Parameters) Read(RuntimeUnitPlan unit)
    {
        var config = unit.Configuration;
        if (!ValidKind(unit.Id.Kind) || unit.Id.KeySegments.Count != 2 || config.EnumerateObject().Count() != 1 ||
            !config.TryGetProperty("inputModule", out var envelope) || envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 2 ||
            !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1 ||
            !envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(parameters.GetRawText()) > ParameterSchema.MaximumConfigurationBytes ||
            parameters.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
        { throw Invalid(); }
        return (number, parameters);
    }

    private static bool ValidKind(string kind)
    {
        if (kind is null || !kind.StartsWith("input.", StringComparison.Ordinal) || kind.Length <= 6)
        { return false; }
        try
        { _ = RuntimeUnitId.Create(kind, "module/input"); return true; }
        catch (ArgumentException)
        { return false; }
    }
    private static ArgumentException Invalid() => new("输入模块未安装、版本不支持或参数/字段无效。");

    private sealed class Factory(InputModuleCatalog catalog, string kind, bool handoff) : IInputUnitFactory
    {
        public string Kind => kind;
        public bool UsesDurableHandoff => handoff;
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit)
        {
            try
            {
                var (module, parameters) = catalog.Resolve(unit);
                return module.RecoveryGroup(new(unit.Id, parameters)) ?? throw Invalid();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            try
            {
                _ = catalog.Describe(unit);
                var (module, parameters) = catalog.Resolve(unit);
                return module.Build(new(unit.Id, parameters), context) ?? throw Invalid();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
        }
    }
}
