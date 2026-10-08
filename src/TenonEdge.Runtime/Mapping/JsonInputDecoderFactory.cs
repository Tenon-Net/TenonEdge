using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

/// <summary>输入字段与可选上游时间；不执行路线换算，数值/时间复用正式映射核心。</summary>
public sealed class JsonInputDecoderFactory : IJsonInputDecoderFactory
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<FieldType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<UpstreamTimeFormat>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<TimeSource>(allowIntegerValues: false),
        },
    };

    public IJsonInputDecoder Build(JsonElement fields, JsonElement upstreamTime = default)
    {
        var definitions = fields.Deserialize<FieldDefinition[]>(Options) ?? throw new ArgumentException("输入字段不能为空。");
        if (definitions.Length is < 1 or > 1000)
        { throw new ArgumentException("输入须定义 1—1000 个字段。"); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<CompiledField>();
        foreach (var field in definitions)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 64 || !keys.Add(field.Key) || field.Type is null)
            { throw new ArgumentException("输入字段名称或固定类型无效。"); }
            compiled.Add(new(field.Key, new JsonFieldPointer(field.Path), new FieldValueConversion(field.Type.Value)));
        }
        UpstreamTimeMapping? time = null;
        if (upstreamTime.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var definition = upstreamTime.Deserialize<TimeDefinition>(Options) ?? throw new ArgumentException("上游时间配置无效。");
            if (definition.Format is null)
            { throw new ArgumentException("上游时间须明确声明格式及 Unix 时间单位。"); }
            time = new UpstreamTimeMapping(definition.Path, definition.Format.Value, definition.Source ?? TimeSource.Upstream);
        }
        return new Decoder(compiled.ToImmutableArray(), time);
    }

    private sealed record TimeDefinition(string Path, UpstreamTimeFormat? Format, TimeSource? Source);
    private sealed record FieldDefinition(string Key, string Path, FieldType? Type);
    private sealed record CompiledField(string Key, JsonFieldPointer Path, FieldValueConversion Conversion);
    private sealed class Decoder(ImmutableArray<CompiledField> fields, UpstreamTimeMapping? time) : IJsonInputDecoder
    {
        public DecodedJsonInput Decode(JsonElement payload, DateTimeOffset receivedAt, ClockSyncState clockState, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(clockState))
            { throw new ArgumentOutOfRangeException(nameof(clockState)); }
            receivedAt = receivedAt.ToUniversalTime();
            var mappedTime = time?.Resolve(payload, receivedAt, clockState)
                ?? new MappedTime(receivedAt, clockState == ClockSyncState.NotSynchronized ? TimeSource.GatewayUnsynced : TimeSource.Gateway, false);
            var values = ImmutableDictionary.CreateBuilder<string, FieldValue>(StringComparer.Ordinal);
            var quality = ImmutableDictionary.CreateBuilder<string, QualityCode>(StringComparer.Ordinal);
            long bytes = 0;
            foreach (var field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!field.Path.TryResolve(payload, out var source))
                { continue; }
                var converted = field.Conversion.Convert(source);
                bytes += 256 + 2L * field.Key.Length + (converted.Value?.BudgetBytes ?? 0);
                if (bytes > 4 * 1024 * 1024)
                { throw new InputMessageSizeException("JSON 输入解码结果超过 4 MiB。"); }
                if (converted.Value is { } value)
                { values.Add(field.Key, value); }
                else
                { quality.Add(field.Key, converted.Error ?? QualityCode.ConversionFailed); }
            }
            return new(values.ToImmutable(), quality.ToImmutable(), mappedTime.CollectedAt, mappedTime.Source, mappedTime.InvalidUpstreamTime);
        }
    }
}
