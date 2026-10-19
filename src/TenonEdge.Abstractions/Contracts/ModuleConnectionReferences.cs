using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>输入、输出和连接共用的冻结引用格式。只读冻结数据，不执行模块或查询草稿。
/// 每个引用对象（含后代）沿用128KiB/128字段上限；同ID须有相同类型/配置，循环拒绝。
/// 计划级连接总数与内置协议语义仍由宿主统一校验。</summary>
public static class ModuleConnectionReferences
{
    /// <summary>返回参数名到直接引用的映射；返回前验证完整后代结构，不将无效树部分返回。</summary>
    public static ImmutableDictionary<string, ModuleConnectionSnapshot> Read(JsonElement envelope)
    {
        var references = ReadDirect(envelope);
        _ = Flatten(references.Values);
        return references;
    }

    /// <summary>完整核对并按ID去重，包含根连接；供引用保护、配额、共享身份及准入复用。
    /// 每次只访问同ID的一个子树；重复副本仍须逐字节匹配规范化配置。</summary>
    public static ImmutableArray<ModuleConnectionSnapshot> Flatten(IEnumerable<ModuleConnectionSnapshot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var known = new Dictionary<long, ModuleConnectionSnapshot>();
        var path = new HashSet<long>();
        var result = ImmutableArray.CreateBuilder<ModuleConnectionSnapshot>();
        foreach (var root in roots)
        { Visit(root); }
        return result.ToImmutable();

        void Visit(ModuleConnectionSnapshot connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (!path.Add(connection.ConnectionId))
            { throw Invalid(); }
            if (known.TryGetValue(connection.ConnectionId, out var previous))
            {
                if (previous.Kind != connection.Kind || previous.Configuration.GetRawText() != connection.Configuration.GetRawText())
                { throw Invalid(); }
            }
            else
            {
                known.Add(connection.ConnectionId, connection);
                result.Add(connection);
                if (connection.Kind is not ("http" or "mqtt" or "modbus"))
                {
                    _ = ModuleConnectionSnapshot.ReadModuleParameters(connection);
                    foreach (var child in ReadDirect(connection.Configuration.GetProperty("connectionModule")).Values)
                    { Visit(child); }
                }
            }
            path.Remove(connection.ConnectionId);
        }
    }

    private static ImmutableDictionary<string, ModuleConnectionSnapshot> ReadDirect(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        { throw Invalid(); }
        if (!envelope.TryGetProperty("connections", out var value))
        { return ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty; }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > ParameterSchema.MaximumParameters ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > ParameterSchema.MaximumConfigurationBytes)
        { throw Invalid(); }
        var result = ImmutableDictionary.CreateBuilder<string, ModuleConnectionSnapshot>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            if (!BusinessKeys.IsValid(property.Name) || item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3 ||
                !item.TryGetProperty("connectionId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number is < 1 or > 9007199254740991L ||
                !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !BusinessKeys.IsValid(kind.GetString()) ||
                !item.TryGetProperty("configuration", out var config) || config.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty(property.Name, out var reference) || reference.ValueKind != JsonValueKind.String ||
                reference.GetString() != number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            { throw Invalid(); }
            if (!result.TryAdd(property.Name, new(number, kind.GetString()!, config)))
            { throw Invalid(); }
        }
        return result.ToImmutable();
    }

    private static ArgumentException Invalid() => new("模块连接引用无效。");
}
