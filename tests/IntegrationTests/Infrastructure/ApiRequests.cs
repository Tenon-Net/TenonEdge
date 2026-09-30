using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>管理 API 的请求路径与 JSON 读写辅助。请求体按 Web 约定（小驼峰）序列化，与宿主的 JSON 设置一致。</summary>
internal static class ApiRequests
{
    public const string Setup = "/api/edge-admin/v1/setup";
    public const string Login = "/api/edge-admin/v1/auth/login";
    public const string Logout = "/api/edge-admin/v1/auth/logout";
    public const string Session = "/api/edge-admin/v1/auth/session";
    public const string ChangePassword = "/api/edge-admin/v1/auth/password";
    public const string ApiDocument = "/api/edge-admin/v1/openapi/v1.json";

    /// <summary>符合密码规则的测试密码；测试不使用任何真实密码。</summary>
    public const string StrongPassword = "Tenon-Edge-2026";

    public const string AnotherStrongPassword = "Gateway-Setup-0930";

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, JsonSerializerOptions.Web, cancellationToken);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web, cancellationToken);

    public const string CsrfHeader = "X-CSRF-Token";

    /// <summary>
    /// 读取登录、首次设置或修改密码返回的会话，并把其中的防 CSRF 令牌设为该客户端后续请求的请求头，与前端的做法相同。
    /// </summary>
    public static async Task<JsonElement> UseSessionAsync(this HttpClient client, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var session = await response.ReadJsonAsync(cancellationToken);
        client.DefaultRequestHeaders.Remove(CsrfHeader);
        client.DefaultRequestHeaders.Add(CsrfHeader, session.GetProperty("csrfToken").GetString());
        return session;
    }

    /// <summary>ProblemDetails 中的业务错误代码。</summary>
    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var problem = await response.ReadJsonAsync(cancellationToken);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static Task<HttpResponseMessage> CompleteSetupAsync(
        this HttpClient client,
        CancellationToken cancellationToken,
        string accountName = "admin",
        string password = StrongPassword,
        string? gatewayName = null) =>
        client.PostJsonAsync(Setup, new { accountName, password, gatewayName }, cancellationToken);

    public static Task<HttpResponseMessage> LoginAsync(
        this HttpClient client,
        string accountName,
        string password,
        CancellationToken cancellationToken) =>
        client.PostJsonAsync(Login, new { accountName, password }, cancellationToken);

    /// <summary>创建一个客户端并以给定账号登录（密码为 <see cref="StrongPassword"/>），已设置防 CSRF 请求头。</summary>
    public static async Task<HttpClient> CreateLoggedInClientAsync(
        this WebApplicationFactory<Program> factory,
        string accountName,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();
        try
        {
            using var login = await client.LoginAsync(accountName, StrongPassword, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(cancellationToken);
    }
}
