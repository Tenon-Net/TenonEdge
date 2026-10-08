using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>协议层借用运行时的固定类型 JSON 字段与显式上游时间解码，不依赖 Runtime 实现程序集。</summary>
public interface IJsonInputDecoderFactory
{
    IJsonInputDecoder Build(JsonElement fields, JsonElement upstreamTime = default);
}

public interface IJsonInputDecoder
{
    DecodedJsonInput Decode(JsonElement payload, DateTimeOffset receivedAt, ClockSyncState clockState, CancellationToken cancellationToken = default);
}

public sealed record DecodedJsonInput(ImmutableDictionary<string, FieldValue> Values, ImmutableDictionary<string, QualityCode> Quality,
    DateTimeOffset CollectedAt, TimeSource TimeSource, bool InvalidUpstreamTime);
