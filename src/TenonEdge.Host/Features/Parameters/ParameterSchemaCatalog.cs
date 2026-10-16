using System.Collections.Immutable;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Host.Features.Parameters;

/// <summary>只持有编译时显式注册的不可变声明。不创建运行计划、适配器、请求作用域或协议会话。</summary>
internal sealed class ParameterSchemaCatalog(IEnumerable<ParameterSchema> schemas)
{
    private readonly ImmutableDictionary<string, ParameterSchema> byId = schemas.ToImmutableDictionary(schema => schema.Id, StringComparer.Ordinal);
    public ParameterSchema? Get(string id) => byId.GetValueOrDefault(id);
}
