using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Runtime.Connections;

/// <summary>新连接类型的声明准入与冻结格式。历史结构读取不依赖已安装模块。
/// 不持有会话、密钥或数据库；实例继续由原 ISharedConnections 管理。
/// 当前无新连接的凭证/嵌套连接解析入口：需要这些状态的必填声明拒绝准入，不能假定已配置。</summary>
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
        var (version, parameters) = ModuleConnectionSnapshot.ReadModuleConfiguration(connection);
        ValidateParameters(connection.Kind, version, parameters);
    }

    private void ValidateParameters(string kind, int version, JsonElement parameters)
    {
        if (!_definitions.TryGetValue((kind, version), out var definition))
        { throw Invalid(); }
        // State cannot be supplied by seed JSON. A required credential/reference is not silently anonymous.
        definition.Parameters.ThrowIfInvalid(parameters);
    }

    private static ArgumentException Invalid() => new("连接模块未安装、版本不支持或冻结参数无效。");
}
