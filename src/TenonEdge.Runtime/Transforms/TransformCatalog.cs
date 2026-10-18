using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Runtime.Transforms;

/// <summary>已安装模块的公开元数据；配置仅包含非秘密标量声明。</summary>
public sealed record TransformModuleDefinition(string Kind, int Version, string DisplayName, ParameterSchema? Parameters, bool CanConfigure);

/// <summary>不可变的编译期模块目录；草稿编译、冻结校验和运行准备共用。不提供动态注册或服务定位。</summary>
public sealed class TransformCatalog
{
    public static TransformCatalog BuiltIn { get; } = new([]);
    private readonly ImmutableDictionary<(string Kind, int Version), IMessageTransformFactory> _factories;

    public ImmutableArray<TransformModuleDefinition> Modules { get; }

    public TransformCatalog(IEnumerable<IMessageTransformFactory> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var builder = ImmutableDictionary.CreateBuilder<(string, int), IMessageTransformFactory>();
        foreach (var factory in new IMessageTransformFactory[] { new FieldMappingTransformFactory() }.Concat(extensions))
        {
            if (factory is null || !ValidKind(factory.Kind) || factory.Version < 1 || string.IsNullOrWhiteSpace(factory.DisplayName) ||
                factory.DisplayName.Length > 128 || factory.DisplayName.Any(char.IsControl) ||
                factory.Parameters is { } schema && (schema.Scope != ParameterScope.Transform ||
                    schema.Parameters.Any(field => field.Type is ParameterType.Credential or ParameterType.ConnectionReference)) ||
                !builder.TryAdd((factory.Kind, factory.Version), factory))
            { throw new ArgumentException("转换模块标识、版本、声明无效或重复。", nameof(extensions)); }
        }
        _factories = builder.ToImmutable();
        Modules = _factories.Values.OrderBy(factory => factory.Kind, StringComparer.Ordinal).ThenBy(factory => factory.Version)
            .Select(factory => new TransformModuleDefinition(factory.Kind, factory.Version, factory.DisplayName, factory.Parameters,
                factory.Parameters is not null || factory.Kind == "transform.map" && factory.Version == 1)).ToImmutableArray();
    }

    public TransformPipeline? ParsePipeline(JsonElement configuration)
    {
        var definitions = ReadStages(configuration);
        if (definitions is null)
        {
            var legacy = FieldMappingConfiguration.ReadStages(configuration);
            return legacy is null ? null : new TransformPipeline(legacy.Select(mapping => new FieldMappingTransform(mapping)));
        }
        return new TransformPipeline(definitions.Value.Select(definition => Build(definition.Kind, definition.Version, definition.Configuration)));
    }

    public bool Contains(string kind, int version) => _factories.ContainsKey((kind, version));

    public IMessageTransform Build(string kind, int version, JsonElement configuration)
    {
        try
        {
            if (!_factories.TryGetValue((kind, version), out var factory))
            { throw new ArgumentException("未安装转换模块。"); }
            factory.Parameters?.ThrowIfInvalid(configuration);
            return new BoundedMessageTransform(factory.Build(configuration.Clone()));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new ArgumentException("转换模块未注册、版本不支持或配置无效。"); }
    }

    /// <summary>允许未完成草稿，但未知字段、秘密旁路、重复属性和资源越界不得保存。
    /// 必填/类型/范围/选项问题留给编译；缺模块不能作为通用 JSON 存储旁路。</summary>
    public JsonElement NormalizeDraft(string kind, int version, JsonElement configuration)
    {
        if (!_factories.TryGetValue((kind, version), out var factory) || factory.Parameters is not { } schema)
        { throw new ArgumentException("未安装可配置的转换模块声明。"); }
        if (configuration.ValueKind != JsonValueKind.Object || configuration.EnumerateObject().Any(property =>
            property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
        { throw new ArgumentException("转换参数须为标量对象。"); }
        var issues = schema.Validate(configuration);
        if (issues.Any(issue => issue.Code is not ("parameter.required" or "parameter.range" or "parameter.integer" or
            "parameter.number" or "parameter.type" or "parameter.choice")))
        { throw new ArgumentException("转换参数包含未知、重复或超限内容。"); }
        return NormalizeStoredConfiguration(configuration);
    }

    public static JsonElement NormalizeStoredConfiguration(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(configuration.GetRawText()) > ParameterSchema.MaximumConfigurationBytes)
        { throw new ArgumentException("转换配置须为不超过128 KiB的对象。"); }
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(configuration));
        return document.RootElement.Clone();
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
        if (!configuration.TryGetProperty("transformStages", out var stages))
        { return null; }
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

    public static bool ValidKind(string? kind) => BusinessKeys.IsValid(kind) && kind!.StartsWith("transform.", StringComparison.Ordinal) && kind.Length > "transform.".Length;
    private sealed record Definition(string Kind, int Version, JsonElement Configuration);
}
