using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record JsonSourceField(string Key, string Path, FieldType? Type, string? Name = null, string? Unit = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SourceTimeMapping(string Path, UpstreamTimeFormat? Format);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MqttSourceDraft(string TopicFilter, JsonSourceField[] Fields, int Qos = 1,
    bool ProcessRetained = false, SourceTimeMapping? UpstreamTime = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HttpSourceDraft(string Path, JsonSourceField[] Fields, int PeriodMs = 5000,
    Dictionary<string, string>? Headers = null, SourceTimeMapping? UpstreamTime = null);

internal static class JsonSourceValidation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static void Validate(MqttSourceDraft source)
    {
        ValidateFields(source.Fields, source.UpstreamTime);
        MqttInputSettings.ValidateSubscription(source.TopicFilter, source.Qos, source.ProcessRetained, Time(source.UpstreamTime));
    }

    public static HttpSourceDraft Normalize(HttpSourceDraft source, ConnectionDraft connection)
    {
        if (source.PeriodMs is < 1000 or > 86400000 || source.Path is null || source.Path.Length > 2048 ||
            source.Headers?.Keys.Any(ConnectionDraftValidation.IsSecretName) == true)
        { throw new ArgumentException("HTTP 周期、路径或非秘密请求头无效。"); }
        ValidateFields(source.Fields, source.UpstreamTime);
        var combined = new Dictionary<string, string>(connection.Http!.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var header in source.Headers ?? [])
        { combined[header.Key] = header.Value; }
        var request = new HttpRequestSettings
        {
            ConnectionId = connection.Id,
            BaseAddress = connection.Http.BaseAddress,
            TimeoutMs = connection.Http.TimeoutMs,
            Path = source.Path,
            Headers = combined.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
        }.ValidateRequest();
        if (QueryHelpers.ParseQuery(request.Endpoint.Query).Keys.Any(ConnectionDraftValidation.IsSecretName))
        { throw new ArgumentException("秘密查询参数须改用连接凭证。"); }
        // Validate source headers independently too: case-insensitive duplicates must not be silently merged.
        _ = (request with { Headers = (source.Headers ?? []).ToImmutableDictionary() }).ValidateRequest();
        return source with { Fields = source.Fields.ToArray(), Headers = source.Headers is null ? null : new(source.Headers, StringComparer.OrdinalIgnoreCase) };
    }

    private static void ValidateFields(JsonSourceField[] fields, SourceTimeMapping? time)
    {
        if (fields is null || fields.Length > 1000)
        { throw new ArgumentException("JSON 来源最多 1000 个字段。"); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null || !BusinessKeys.IsValid(field.Key) || !keys.Add(field.Key) || field.Type is null || !Enum.IsDefined(field.Type.Value) ||
                field.Name?.Length > 64 || field.Unit?.Length > 64 ||
                field.Name?.Any(char.IsControl) == true || field.Unit?.Any(char.IsControl) == true)
            { throw new ArgumentException("JSON 字段 key、类型、显示名或单位无效。"); }
            _ = new JsonFieldPointer(field.Path);
        }
        if (time is not null)
        {
            if (time.Format is null)
            { throw new ArgumentException("上游时间须明确声明格式及 Unix 时间单位。"); }
            _ = new UpstreamTimeMapping(time.Path, time.Format.Value, TimeSource.Upstream);
        }
        if (fields.Length != 0)
        {
            // Metadata is intentionally excluded from the runtime decoder definition.
            _ = new JsonInputDecoderFactory().Build(JsonSerializer.SerializeToElement(
                fields.Select(field => new { field.Key, field.Path, field.Type }), Json), Time(time));
        }
    }

    internal static JsonElement Time(SourceTimeMapping? time) =>
        time is null ? default : JsonSerializer.SerializeToElement(time, Json);
}
