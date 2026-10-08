using System.Globalization;
using System.Text.Json;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// RFC 6901 字符串形式的固定字段定位；在构建时验证，不执行查询、脚本或 URI 解码。
/// 返回值借用输入文档的生命周期，不克隆报文，不保存上一次解析结果。
/// </summary>
public sealed class JsonFieldPointer
{
    private readonly string[] _tokens;

    public JsonFieldPointer(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        { _tokens = []; return; }
        if (path[0] != '/')
        { throw new ArgumentException("JSON Pointer 必须为空或以 / 开头。", nameof(path)); }
        _tokens = path[1..].Split('/');
        for (var index = 0; index < _tokens.Length; index++)
        {
            var token = _tokens[index];
            for (var offset = 0; offset < token.Length; offset++)
            {
                if (token[offset] != '~')
                { continue; }
                if (++offset == token.Length || token[offset] is not ('0' or '1'))
                {
                    throw new ArgumentException("JSON Pointer 转义仅允许 ~0 与 ~1。", nameof(path));
                }
            }
            // 顺序很重要：~01 表示字面量 ~1，不是 /。
            _tokens[index] = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
    }

    public bool TryResolve(JsonElement root, out JsonElement value)
    {
        value = default;
        var current = root;
        foreach (var token in _tokens)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                // 同名成员无法唯一定位；不默默选择其中之一。
                var found = false;
                var next = default(JsonElement);
                foreach (var property in current.EnumerateObject())
                {
                    if (!property.NameEquals(token))
                    { continue; }
                    if (found)
                    { return false; }
                    found = true;
                    next = property.Value;
                }
                if (!found)
                { return false; }
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (token.Length == 0 || (token.Length > 1 && token[0] == '0') ||
                    token.Any(character => character is < '0' or > '9') ||
                    !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index >= current.GetArrayLength())
                { return false; }
                current = current[index];
            }
            else
            { return false; }
        }
        if (current.ValueKind == JsonValueKind.Undefined)
        { return false; }
        value = current;
        return true;
    }
}
