using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Runtime.Transforms;

/// <summary>不可变的编译期模块目录；种子冻结校验和运行准备共用。不提供动态注册或服务定位。</summary>
public sealed class TransformCatalog
{
    public static TransformCatalog BuiltIn { get; } = new([]);
    private readonly ImmutableDictionary<(string Kind, int Version), IMessageTransformFactory> _factories;

    public TransformCatalog(IEnumerable<IMessageTransformFactory> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var builder = ImmutableDictionary.CreateBuilder<(string, int), IMessageTransformFactory>();
        foreach (var factory in new IMessageTransformFactory[] { new FieldMappingTransformFactory() }.Concat(extensions))
        {
            if (factory is null || !ValidKind(factory.Kind) || factory.Version < 1 ||
                factory.Parameters is { } schema && (schema.Scope != ParameterScope.Transform ||
                    schema.Parameters.Any(field => field.Type is ParameterType.Credential or ParameterType.ConnectionReference)) ||
                !builder.TryAdd((factory.Kind, factory.Version), factory))
            { throw new ArgumentException("转换模块标识、版本、声明无效或重复。", nameof(extensions)); }
        }
        _factories = builder.ToImmutable();
    }

    public TransformPipeline? ParsePipeline(JsonElement configuration)
    {
        var definitions = ReadStages(configuration);
        if (definitions is null)
        {
            var legacy = FieldMappingConfiguration.ReadStages(configuration);
            return legacy is null ? null : new TransformPipeline(legacy.Select(mapping => new FieldMappingTransform(mapping)));
        }
        try
        {
            return new TransformPipeline(definitions.Value.Select(definition =>
            {
                if (!_factories.TryGetValue((definition.Kind, definition.Version), out var factory))
                { throw new ArgumentException("未注册所需版本的转换模块。"); }
                factory.Parameters?.ThrowIfInvalid(definition.Configuration);
                return factory.Build(definition.Configuration.Clone());
            }));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new ArgumentException("转换模块未注册、版本不支持或配置无效。"); }
    }

    /// <summary>仅用于已持久化且通过哈希核对的历史读取。保留不可用扩展的原配置，不执行扩展代码。
    /// 新冻结与运行准备必须另经本目录 ParsePipeline，不能把此方法当作配置准入。</summary>
    public static void ValidateStoredShape(JsonElement configuration)
    {
        if (ReadStages(configuration) is not { } definitions)
        { _ = FieldMappingConfiguration.ParsePipeline(configuration); return; }
        // 内置规则的校验不依赖部署模块，历史损坏仍须检出。
        var mapping = new FieldMappingTransformFactory();
        foreach (var definition in definitions.Where(value => value.Kind == mapping.Kind && value.Version == mapping.Version))
        { _ = mapping.Build(definition.Configuration); }
    }

    private static ImmutableArray<Definition>? ReadStages(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("transformStages", out var stages)) { return null; }
        if (configuration.TryGetProperty("mapping", out _) || configuration.TryGetProperty("mappingStages", out _) ||
            stages.ValueKind != JsonValueKind.Array || stages.GetArrayLength() is < 1 or > TransformPipeline.MaximumStages)
        { throw new ArgumentException("transformStages 须为 1—48 个阶段，且不能与旧映射同时声明。"); }
        var builder = ImmutableArray.CreateBuilder<Definition>();
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object || stage.EnumerateObject().Count() != 3 ||
                !stage.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !ValidKind(kind.GetString()) ||
                !stage.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1 ||
                !stage.TryGetProperty("configuration", out var values) || values.ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(values.GetRawText()) > ParameterSchema.MaximumConfigurationBytes)
            { throw new ArgumentException("转换阶段格式无效。"); }
            builder.Add(new(kind.GetString()!, number, values));
        }
        return builder.ToImmutable();
    }

    private static bool ValidKind(string? kind) => BusinessKeys.IsValid(kind) && kind!.StartsWith("transform.", StringComparison.Ordinal) && kind.Length > "transform.".Length;
    private sealed record Definition(string Kind, int Version, JsonElement Configuration);
}
