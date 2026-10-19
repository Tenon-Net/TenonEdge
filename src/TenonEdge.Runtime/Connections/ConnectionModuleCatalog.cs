using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Modules;

namespace TenonEdge.Runtime.Connections;

/// <summary>新连接类型的声明准入与冻结格式。历史结构读取不依赖已安装模块。
/// 不持有会话、密钥或数据库；实例继续由原 ISharedConnections 管理。
/// 显式绑定的凭证由独立解析器在使用前核对；所有后代引用同样经声明准入。</summary>
public sealed class ConnectionModuleCatalog
{
    public static ConnectionModuleCatalog Empty { get; } = new([]);
    private readonly ImmutableDictionary<(string Kind, int Version), ConnectionModuleDefinition> _definitions;
    public ImmutableArray<ConnectionModuleDefinition> Modules { get; }

    public ConnectionModuleCatalog(IEnumerable<ConnectionModuleDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var entries = ImmutableDictionary.CreateBuilder<(string, int), ConnectionModuleDefinition>();
        foreach (var definition in definitions)
        {
            if (definition is null || !entries.TryAdd((definition.Kind, definition.Version), definition))
            { throw new ArgumentException("连接模块声明重复或无效。", nameof(definitions)); }
        }
        _definitions = entries.ToImmutable();
        Modules = _definitions.Values.OrderBy(value => value.Kind, StringComparer.Ordinal).ThenBy(value => value.Version).ToImmutableArray();
    }

    public ModuleConnectionSnapshot Freeze(long connectionId, string kind, int version, JsonElement parameters)
    {
        // Validate before canonicalizing: normalization must not erase duplicate/unknown fields.
        ValidateParameters(kind, version, parameters);
        return new(connectionId, kind, JsonSerializer.SerializeToElement(new { connectionModule = new { version, parameters } }));
    }

    public void Validate(ModuleConnectionSnapshot connection)
    {
        if (!ConnectionModuleDefinition.IsModuleKind(connection.Kind))
        { throw Invalid(); }
        foreach (var current in ModuleConnectionReferences.Flatten([connection]).Where(value => ConnectionModuleDefinition.IsModuleKind(value.Kind)))
        {
            var (version, parameters) = ModuleConnectionSnapshot.ReadModuleConfiguration(current);
            ValidateParameters(current.Kind, version, parameters,
                ModuleConnectionReferences.Read(current.Configuration.GetProperty("connectionModule")));
        }
    }

    public ConnectionModuleDefinition Definition(ModuleConnectionSnapshot connection)
    {
        var (version, _) = ModuleConnectionSnapshot.ReadModuleConfiguration(connection);
        return _definitions.GetValueOrDefault((connection.Kind, version)) ?? throw Invalid();
    }

    /// <summary>只有运行工厂的准备/重建调用；编辑、发现、结构编译不读取秘密。
    /// 曾配置的可选凭证不可因损坏/换址而退为匿名，必填凭证必须真正可解密。</summary>
    public void ValidateForUse(ModuleConnectionSnapshot connection, IModuleCredentialResolver? credentials)
    {
        Validate(connection);
        foreach (var current in ModuleConnectionReferences.Flatten([connection]).Where(value => ConnectionModuleDefinition.IsModuleKind(value.Kind)))
        {
            var definition = Definition(current);
            var fields = definition.Parameters.Parameters.Where(field => field.Type == ParameterType.Credential).ToArray();
            if (fields.Length == 0)
            { continue; }
            var identity = credentials?.DescribeModule(current.ConnectionId);
            if (identity is null && !fields.Any(field => field.Required))
            { continue; }
            var resolved = credentials?.ResolveModule(current) ?? throw Invalid();
            if (fields.Any(field => field.Required && !resolved.Names.Contains(field.Name, StringComparer.Ordinal)))
            { throw Invalid(); }
        }
    }

    private void ValidateParameters(string kind, int version, JsonElement parameters,
        IReadOnlyDictionary<string, ModuleConnectionSnapshot>? references = null)
    {
        if (!_definitions.TryGetValue((kind, version), out var definition))
        { throw Invalid(); }
        // A declared binding permits editing without secrets. Actual credentials are
        // checked at runtime preparation, not supplied by untrusted snapshot JSON.
        ModuleConnections.Validate(definition.Parameters, parameters,
            references ?? ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty,
            configuredCredentials: definition.RecipientParameters.IsEmpty ? null :
                definition.Parameters.Parameters.Where(field => field.Type == ParameterType.Credential)
                    .Select(field => field.Name).ToHashSet(StringComparer.Ordinal));
    }

    private static ArgumentException Invalid() => new("连接模块未安装、版本不支持或冻结参数无效。");
}
