using System.Net;
using System.Text.Json;

namespace TenonEdge.IntegrationTests.Infrastructure;

internal static class ProblemAssert
{
    /// <summary>
    /// 断言响应是给定状态码的 RFC 9457 ProblemDetails（而不是 200 包装的失败或空响应），返回响应正文。
    /// </summary>
    public static async Task<string> HasStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));

        return body;
    }
}
