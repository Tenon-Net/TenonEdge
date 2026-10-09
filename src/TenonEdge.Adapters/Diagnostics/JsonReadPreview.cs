using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Diagnostics;

public sealed record JsonPreviewField(string Key, string? Type, string? Text, string Quality, string? Reason, bool UnsafeJavaScriptInteger);
public sealed record JsonReadPreviewResult(bool Success, string Code, int? StatusCode, string? RawJson,
    int RawBytes, bool RawTruncated, bool FieldsTruncated, bool Redacted, DateTimeOffset? ReceivedAt,
    DateTimeOffset? CollectedAt, TimeSource? TimeSource, bool InvalidUpstreamTime, JsonPreviewField[] Fields)
{
    public bool CanUseAsSample => Success && !RawTruncated && !FieldsTruncated && !Redacted;
    internal static JsonReadPreviewResult Failure(string code, int? status = null) =>
        new(false, code, status, null, 0, false, false, false, null, null, null, false, []);
    public override string ToString() => $"JSON read preview ({Code}; body omitted)";
}

/// <summary>已验证输入文档的内存展示。数值不经 double；输出按实际 JSON 转义字节有界。
/// 凭证回显或敏感字段令整个展示隐藏，不提供可逆替换或把秘密带到解码结果。</summary>
internal static class JsonReadPreview
{
    internal const int MaximumDisplayBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        { "password", "passwd", "pwd", "secret", "clientsecret", "token", "accesstoken", "refreshtoken", "apikey", "authorization", "cookie", "setcookie" };

    internal static JsonReadPreviewResult Render(JsonElement root, IJsonInputDecoder? decoder, HttpCredential? credential,
        ClockSyncState clockState, DateTimeOffset receivedAt, int? statusCode, CancellationToken cancellationToken)
    {
        var raw = root.GetRawText();
        var rawBytes = Encoding.UTF8.GetByteCount(raw);
        var secrets = CredentialForms(credential);
        if (ContainsSensitive(root, secrets, cancellationToken))
        { return new(false, "payload.redacted", statusCode, null, rawBytes, false, false, true, receivedAt, null, null, false, []); }
        var decoded = decoder?.Decode(root, receivedAt, clockState, cancellationToken);
        var rawDisplay = Limit(raw, 32 * 1024);
        var fields = new List<JsonPreviewField>();
        var bytes = 1024 + JsonSerializer.SerializeToUtf8Bytes(rawDisplay, Json).Length;
        var truncated = false;
        if (decoded is not null)
        {
            foreach (var key in decoded.Values.Keys.Concat(decoded.Quality.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = decoded.Values.GetValueOrDefault(key);
                var reason = decoded.Quality.TryGetValue(key, out var quality) ? JsonSerializer.Serialize(quality).Trim('"') : null;
                var field = new JsonPreviewField(key, value?.Type switch
                { FieldType.Boolean => "boolean", FieldType.Integer => "integer", FieldType.Number => "number", FieldType.String => "string", _ => null },
                    value?.ToString(), value is null ? "bad" : "good", reason,
                    value?.Type == FieldType.Integer && value.TryGetExactNumber(out var number) && (number > 9007199254740991m || number < -9007199254740991m));
                var size = JsonSerializer.SerializeToUtf8Bytes(field, Json).Length + 1;
                if (bytes + size > MaximumDisplayBytes)
                { truncated = true; break; }
                fields.Add(field);
                bytes += size;
            }
        }
        return new(true, "success", statusCode, rawDisplay, rawBytes, rawDisplay.Length != raw.Length, truncated, false,
            receivedAt, decoded?.CollectedAt ?? receivedAt,
            decoded?.TimeSource ?? (clockState == ClockSyncState.NotSynchronized ? TimeSource.GatewayUnsynced : TimeSource.Gateway),
            decoded?.InvalidUpstreamTime ?? false, fields.ToArray());
    }

    private static string Limit(string text, int bytes)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(text, Json).Length <= bytes)
        { return text; }
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (JsonSerializer.SerializeToUtf8Bytes(text[..Boundary(middle)], Json).Length <= bytes)
            { low = middle; }
            else
            { high = middle - 1; }
        }
        return text[..Boundary(low)];
        int Boundary(int index) => index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;
    }

    private static string[] CredentialForms(HttpCredential? credential)
    {
        if (credential is null)
        { return []; }
        var forms = new List<string> { credential.Secret, Uri.EscapeDataString(credential.Secret) };
        if (credential.Identity.Kind == HttpCredentialKind.Basic)
        { forms.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Identity.Username + ":" + credential.Secret))); }
        return forms.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool ContainsSensitive(JsonElement value, string[] secrets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (SecretKeys.Contains(property.Name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)) ||
                    secrets.Any(secret => property.Name.Contains(secret, StringComparison.Ordinal)) || ContainsSensitive(property.Value, secrets, token))
                { return true; }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            { if (ContainsSensitive(item, secrets, token)) { return true; } }
        }
        else
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)))
            { return true; }
        }
        return false;
    }
}
