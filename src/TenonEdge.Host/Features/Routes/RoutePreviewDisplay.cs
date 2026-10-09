using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Routes;

/// <summary>整个响应共用 64 KiB JSON 显示预算（含转义与结构），计算仍执行所有合法路径。
/// 数值以类型 + 原文文本返回；截断正文仅供显示，不能当作完整测试发送报文。</summary>
internal sealed class RoutePreviewDisplay
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RouteSamplePreviewResult _basis;
    private readonly List<PathState> _paths = [];
    private RoutePreviewStage? _source;
    private int _remaining = MaximumBytes;
    private int _succeeded;
    private bool _truncated;

    internal RoutePreviewDisplay(string version, long sourceId, EdgeMessage sample, int paths)
    {
        _basis = new(version, sourceId, sample.SourceMessageId, sample.CollectedAt, sample.TimeSource, paths, paths, false, null, []);
        _ = Take(_basis);
    }

    internal sealed class PathState(RoutePreviewPath basis, bool visible)
    {
        internal RoutePreviewPath Basis { get; } = basis;
        internal bool Visible { get; } = visible;
        internal bool Succeeded { get; set; }
        internal List<RoutePreviewStage> Stages { get; } = [];
        internal List<RouteTopologyIssue> Issues { get; } = [];
        internal RoutePreviewOutput? Output { get; set; }
    }

    internal PathState Path(ImmutableArray<string> nodes)
    {
        var basis = new RoutePreviewPath(nodes, false, [], null, []);
        var state = new PathState(basis, Take(basis));
        if (state.Visible)
        { _paths.Add(state); }
        return state;
    }

    internal void Source(ImmutableArray<MappedFieldSchema> schema, EdgeMessage source, CancellationToken token) =>
        _source = Stage("", schema, new(source, []), token);

    internal void Stage(PathState path, string node, ImmutableArray<MappedFieldSchema> schema, FieldMappingResult result, CancellationToken token)
    {
        if (path.Visible && Stage(node, schema, result, token) is { } stage)
        { path.Stages.Add(stage); }
    }

    private RoutePreviewStage? Stage(string node, ImmutableArray<MappedFieldSchema> schema, FieldMappingResult result, CancellationToken token)
    {
        var basis = new RoutePreviewStage(node, false, [], [], false);
        if (!Take(basis))
        { return null; }
        var fields = new List<RoutePreviewField>();
        var issues = new List<FieldMappingIssue>();
        var truncated = false;
        // Report errors before values so a large good field cannot hide required-failure explanations.
        foreach (var issue in result.Issues)
        {
            token.ThrowIfCancellationRequested();
            if (!Take(issue))
            { truncated = true; break; }
            issues.Add(issue);
        }
        if (result.Message is { } message)
        {
            foreach (var field in schema)
            {
                token.ThrowIfCancellationRequested();
                var value = message.Values.GetValueOrDefault(field.Key);
                QualityCode? reason = message.Quality.TryGetValue(field.Key, out var bad) ? bad : null;
                var text = value?.ToString();
                // Avoid serializing a multi-MiB string just to discover it cannot fit in the remaining display.
                if (text?.Length > _remaining)
                { truncated = _truncated = true; break; }
                var display = new RoutePreviewField(field.Key, (value?.Type ?? field.Type).ToString().ToLowerInvariant(), field.Unit,
                    text, value is not null ? "good" : reason is not null ? "bad" : "missing", reason);
                if (!Take(display))
                { truncated = true; break; }
                fields.Add(display);
            }
        }
        return basis with { Succeeded = result.Message is not null, Fields = fields.ToArray(), Issues = issues.ToArray(), Truncated = truncated };
    }

    internal void Output(PathState path, string node, string kind, Guid deliveryId, string address, int? qos,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (!path.Visible)
        { return; }
        var basis = new RoutePreviewOutput(node, kind, deliveryId, address, qos, headers, "", body.Length, false);
        if (!Take(basis))
        { return; }
        var text = Encoding.UTF8.GetString(body);
        var length = Math.Min(text.Length, _remaining);
        // Count actual JSON escaping, preserving surrogate pairs. This bound applies to display, never execution.
        var low = 0;
        var high = length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var size = JsonSerializer.SerializeToUtf8Bytes(text[..Boundary(middle)], Json).Length + 1;
            if (size <= _remaining)
            { low = middle; }
            else
            { high = middle - 1; }
        }
        var shown = text[..Boundary(low)];
        if (!Take(shown))
        { shown = ""; }
        var truncated = shown.Length != text.Length;
        _truncated |= truncated;
        path.Output = basis with { Body = shown, Truncated = truncated };
        int Boundary(int index) => index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;
    }

    internal void Complete(PathState path) { path.Succeeded = true; _succeeded++; }
    internal void Fail(PathState path, RouteTopologyIssue issue)
    {
        if (path.Visible && Take(issue))
        { path.Issues.Add(issue); }
    }

    internal RouteSamplePreviewResult Result() => _basis with
    {
        Source = _source,
        SucceededPaths = _succeeded,
        Truncated = _truncated,
        Paths = _paths.Select(path => path.Basis with
        {
            Succeeded = path.Succeeded,
            Stages = path.Stages.ToArray(),
            Output = path.Output,
            Issues = path.Issues.ToArray(),
        }).ToArray(),
    };

    private bool Take<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json).Length + 1;
        if (bytes > _remaining)
        { _truncated = true; return false; }
        _remaining -= bytes;
        return true;
    }
}
