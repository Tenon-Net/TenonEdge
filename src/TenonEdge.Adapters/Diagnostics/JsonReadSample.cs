using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Diagnostics;

/// <summary>供纯路线计算使用的完整消息，不是管理诊断展示 DTO；不得直接回显或记录正文。</summary>
public sealed record JsonReadSampleResult(string Code, EdgeMessage? Message, bool InvalidUpstreamTime)
{
    internal static JsonReadSampleResult Failure(string code) => new(code, null, false);
    public override string ToString() => $"JSON input sample ({Code}; body omitted)";
}

internal static class JsonReadSample
{
    // root 已经过协议输入边界（1 MiB、深度 64、UTF-8）校验；不受 64 KiB 展示预算裁剪。
    internal static JsonReadSampleResult Decode(JsonElement root, IJsonInputDecoder decoder, HttpCredential? credential,
        string inputKey, string deviceKey, ClockSyncState clockState, DateTimeOffset receivedAt,
        CancellationToken token, string[]? additionalSecrets = null)
    {
        if (JsonReadPreview.IsSensitive(root, credential, token, additionalSecrets))
        { return JsonReadSampleResult.Failure("payload.redacted"); }
        var decoded = decoder.Decode(root, receivedAt, clockState, token);
        token.ThrowIfCancellationRequested();
        var sample = new EdgeMessage(Guid.CreateVersion7(receivedAt), inputKey, deviceKey,
            decoded.CollectedAt, decoded.TimeSource,
            receivedAt, decoded.Values, decoded.Quality,
            RawJsonPayload.CopyFrom(root), DiagnosticSampleAccess.Safe, new(decoded.InvalidUpstreamTime));
        return new("success", sample, decoded.InvalidUpstreamTime);
    }
}
