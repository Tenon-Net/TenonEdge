using System.Buffers;
using System.Text.Json;
using System.Text.Unicode;

namespace TenonEdge.Adapters.Http;

/// <summary>HTTP 拉取单个 JSON 文档。调用者负责总期限和释放响应及返回的文档；不记录外部正文。</summary>
public static class HttpInputResponse
{
    public const int MaximumBodyBytes = 1024 * 1024;
    public const int MaximumJsonDepth = 64;

    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        { throw new HttpInputResponseException(HttpInputFailure.Status, $"HTTP {(int)response.StatusCode} 拉取失败。"); }
        if (response.Content.Headers.ContentLength > MaximumBodyBytes)
        { throw new HttpInputResponseException(HttpInputFailure.TooLarge, "HTTP 输入正文超过 1 MiB。"); }
        // 传输不自动解压；不把编码内容当作 JSON，也不为压缩炸弹建立解压缓冲。
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        { throw new HttpInputResponseException(HttpInputFailure.Encoding, "HTTP 输入不支持此正文编码。"); }
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumBodyBytes + 1);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var length = 0;
            while (length <= MaximumBodyBytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length, MaximumBodyBytes + 1 - length), cancellationToken);
                if (count == 0)
                { break; }
                length += count;
            }
            if (length > MaximumBodyBytes)
            { throw new HttpInputResponseException(HttpInputFailure.TooLarge, "HTTP 输入正文超过 1 MiB。"); }
            // JsonDocument 延迟解码字符串，ParseValue 本身可能接受非法 UTF-8；在输入边界拒绝。
            if (!Utf8.IsValid(buffer.AsSpan(0, length)))
            { throw new JsonException(); }
            // ParseValue 独立持有文档数据，不能把池缓冲作为 JsonDocument 的长期 backing memory。
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), new JsonReaderOptions { MaxDepth = MaximumJsonDepth });
            var document = JsonDocument.ParseValue(ref reader);
            try
            {
                if (reader.Read())
                { throw new JsonException(); }
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException)
        { throw new HttpInputResponseException(HttpInputFailure.InvalidJson, "HTTP 输入不是有效的单个 JSON 文档或超过深度上限。"); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

public enum HttpInputFailure { Status, TooLarge, Encoding, InvalidJson }

/// <summary>可分类的数据/状态错误；网络和取消异常保持原类型，由输入循环分别处理，不向用户回显原始正文。</summary>
public sealed class HttpInputResponseException(HttpInputFailure failure, string message) : Exception(message)
{
    public HttpInputFailure Failure { get; } = failure;
}
