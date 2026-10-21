using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Parameters;

namespace TenonEdge.Host.Features.Connections;

internal enum ConnectionKind { Modbus, Mqtt, Http, Module }

/// <summary>明确的连接类型/版本与非秘密标量。历史读取只核对结构，新准入另经本机声明目录。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModuleConnectionDraft(string Kind, int Version, JsonElement Parameters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, long>? Connections = null)
{
    internal IEnumerable<long> ConnectionIds => Connections?.Values ?? Enumerable.Empty<long>();

    internal ModuleConnectionSnapshot Snapshot(long id, IReadOnlyDictionary<long, ConnectionDraft>? available = null,
        IReadOnlySet<long>? mqttCredentials = null, bool requireEnabled = false)
    {
        var candidate = available?.ToDictionary() ?? [];
        candidate[id] = candidate.TryGetValue(id, out var current)
            ? current with { Module = this }
            : new(id, "module", "Module", ConnectionKind.Module, true, null, null, null, this);
        return ModuleDraftConnections.Freeze(id, candidate, mqttCredentials, requireEnabled);
    }

    internal ModuleConnectionDraft Normalize(long id)
    {
        if (Parameters.ValueKind != JsonValueKind.Object ||
            System.Text.Encoding.UTF8.GetByteCount(Parameters.GetRawText()) > TenonEdge.Abstractions.Parameters.ParameterSchema.MaximumConfigurationBytes)
        { throw new ArgumentException("连接模块参数须为有界的非秘密标量对象。"); }
        // Shape-only history/export admission does not look up mutable connections.
        var parameters = ModuleDraftConnections.Combine(Parameters, Connections);
        _ = ModuleConnectionSnapshot.ReadModuleConfiguration(new(id, Kind,
            JsonSerializer.SerializeToElement(new { connectionModule = new { version = Version, parameters } })));
        return this with { Parameters = Parameters.Clone(), Connections = Connections?.ToDictionary(StringComparer.Ordinal) };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModbusConnectionDraft(string Host, int Port = 502, ModbusFrameFormat Format = ModbusFrameFormat.Tcp,
    int ConnectTimeoutMs = 5000, int ReadTimeoutMs = 1000, int RequestIntervalMs = 0, int RtuSilenceMs = 500);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MqttConnectionDraft(string Host, int Port = 1883, bool Tls = false, string? ClientId = null,
    int TimeoutMs = 5000, int PingIntervalMs = 15000);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HttpConnectionDraft(string BaseAddress, int TimeoutMs = 5000, Dictionary<string, string>? Headers = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConnectionDraftRequest(string ExpectedVersion, string Key, string Name, ConnectionKind Kind,
    bool Enabled = true, ModbusConnectionDraft? Modbus = null, MqttConnectionDraft? Mqtt = null, HttpConnectionDraft? Http = null,
    ModuleConnectionDraft? Module = null);

internal sealed record ConnectionDraft(long Id, string Key, string Name, ConnectionKind Kind, bool Enabled,
    ModbusConnectionDraft? Modbus, MqttConnectionDraft? Mqtt, HttpConnectionDraft? Http,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModuleConnectionDraft? Module = null)
{
    internal bool SameKind(ConnectionDraft other) => Kind == other.Kind && Module?.Kind == other.Module?.Kind;
}
internal sealed record ConnectionDraftList(string Version, ConnectionDraft[] Connections);
internal sealed record ConnectionDraftResult(string Version, ConnectionDraft Connection);

internal static class ConnectionDraftValidation
{
    public static ConnectionDraft Normalize(long id, ConnectionDraftRequest request)
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new ArgumentException("草稿版本号必填，请重新读取草稿。"); }
        if (!BusinessKeys.IsValid(request.Key))
        { throw new ArgumentException("连接 key 须为 1—64 位字母、数字、点、横线或下划线，首位为字母或数字。"); }
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 || request.Name.Any(char.IsControl))
        { throw new ArgumentException("连接名称须为 1—64 个字符，不能包含控制字符。"); }
        if (!Enum.IsDefined(request.Kind) ||
            (request.Modbus is not null ? 1 : 0) + (request.Mqtt is not null ? 1 : 0) + (request.Http is not null ? 1 : 0) + (request.Module is not null ? 1 : 0) != 1)
        { throw new ArgumentException("须且只能提供所选协议的连接参数。"); }
        ModbusConnectionDraft? modbus = null;
        MqttConnectionDraft? mqtt = null;
        HttpConnectionDraft? http = null;
        ModuleConnectionDraft? module = null;
        switch (request.Kind)
        {
            case ConnectionKind.Modbus when request.Modbus is { } value:
                var m = new ModbusConnectionSettings
                {
                    ConnectionId = id,
                    Host = value.Host,
                    Port = value.Port,
                    Format = value.Format,
                    ConnectTimeoutMs = value.ConnectTimeoutMs,
                    ReadTimeoutMs = value.ReadTimeoutMs,
                    RequestIntervalMs = value.RequestIntervalMs,
                    RtuSilenceMs = value.RtuSilenceMs
                }.Validate();
                modbus = value with { Host = m.Host };
                break;
            case ConnectionKind.Mqtt when request.Mqtt is { } value:
                if (value.Host != value.Host?.Trim())
                { throw new ArgumentException("MQTT 主机不能有首尾空格。"); }
                var q = new MqttConnectionSettings
                {
                    ConnectionId = id,
                    Host = value.Host!,
                    Port = value.Port,
                    Tls = value.Tls,
                    ClientId = value.ClientId,
                    TimeoutMs = value.TimeoutMs,
                    PingIntervalMs = value.PingIntervalMs
                }.ValidateConnection();
                mqtt = value with { Host = q.Host.ToLowerInvariant() };
                break;
            case ConnectionKind.Http when request.Http is { } value:
                if (value.BaseAddress is null || value.BaseAddress.Length > 2048 || value.Headers?.Keys.Any(IsSecretName) == true)
                { throw new ArgumentException("基础地址无效或请求头包含秘密名称，请改用凭证。"); }
                var h = new HttpRequestSettings
                {
                    ConnectionId = id,
                    BaseAddress = value.BaseAddress,
                    TimeoutMs = value.TimeoutMs,
                    Headers = (value.Headers ?? []).ToImmutableDictionary()
                }.ValidateRequest();
                http = value with { BaseAddress = h.BaseAddress, Headers = h.Headers.ToDictionary() };
                break;
            case ConnectionKind.Module when request.Module is { } value:
                module = value.Normalize(id);
                break;
            default:
                throw new ArgumentException("协议类型与连接参数不一致。");
        }
        return new(id, request.Key, request.Name.Trim(), request.Kind, request.Enabled, modbus, mqtt, http, module);
    }

    internal static bool IsSecretName(string name)
    {
        var compact = string.Concat(name.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
        return compact.Contains("password", StringComparison.Ordinal) || compact.Contains("secret", StringComparison.Ordinal) ||
            compact.Contains("token", StringComparison.Ordinal) || compact.Contains("apikey", StringComparison.Ordinal) ||
            compact.Contains("accesskey", StringComparison.Ordinal);
    }

}
