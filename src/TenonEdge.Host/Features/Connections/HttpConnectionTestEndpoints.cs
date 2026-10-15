using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HttpTestCredential(HttpCredentialKind Kind, string? Secret = null, string? Username = null, string? Name = null,
    ClientCertificateUpload? Certificate = null)
{
    public override string ToString() => "HTTP test credential (secret omitted)";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HttpConnectionTestRequest(HttpConnectionDraft Connection, long? ConnectionId = null,
    ConnectionTestAuthentication Authentication = ConnectionTestAuthentication.None, HttpTestCredential? Credential = null)
{
    public override string ToString() => "HTTP connection test request (secret omitted)";
}

internal static class HttpConnectionTestEndpoints
{
    public static RouteGroupBuilder MapHttpConnectionTestEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/test/http", Test).WithTags("Connections")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("testHttpConnection")
            .WithSummary("通过共享会话串行测试未保存的 HTTP 参数，只 GET 基础地址，不保存、不读取正文或跟随重定向")
            .WithDescription("显式测试重建该会话空闲客户端；运行参数不匹配时拒绝借用。stored 使用已存凭证且接收方必须匹配。HTTP 状态不能单独证明认证有效。")
            .WithMetadata(new RequestBodyLimit(256 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<HttpConnectionTestResult>, ValidationProblem, ProblemHttpResult>> Test(
        HttpConnectionTestRequest request, HttpConnectionTest probe, HttpCredentialStore credentials, ConnectionDraftStore drafts,
        SnowflakeIdGenerator ids, PreviewOperationBudget budget, TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var value = request.Connection ?? throw new ArgumentException("缺少 HTTP 参数。");
            if (!Enum.IsDefined(request.Authentication) ||
                request.Authentication == ConnectionTestAuthentication.Stored && (request.ConnectionId is null || request.Credential is not null) ||
                request.Authentication == ConnectionTestAuthentication.None && request.Credential is not null ||
                request.Authentication == ConnectionTestAuthentication.Provided && request.Credential is null ||
                value.BaseAddress is null || value.BaseAddress.Length > 2048 || value.Headers?.Keys.Any(ConnectionDraftValidation.IsSecretName) == true)
            { throw new ArgumentException("HTTP 参数或认证选择无效。"); }
            var settings = new HttpRequestSettings
            {
                ConnectionId = request.ConnectionId ?? ids.NextId(),
                BaseAddress = value.BaseAddress,
                TimeoutMs = value.TimeoutMs,
                Headers = (value.Headers ?? []).ToImmutableDictionary(),
            }.ValidateRequest();
            if (request.ConnectionId is not null && !drafts.Read().Connections.Any(item => item.Id == request.ConnectionId && item.Kind == ConnectionKind.Http))
            { return ApiProblems.Problem(409, "connection.notFound", "请选择已有 HTTP 连接或创建新的测试表单。"); }
            var credential = request.Authentication switch
            {
                ConnectionTestAuthentication.Stored => credentials.ResolveHttp(settings.ConnectionId, settings.BaseAddress),
                ConnectionTestAuthentication.Provided => Transient(request.Credential!, time.GetUtcNow()),
                _ => null,
            };
            if (request.Authentication == ConnectionTestAuthentication.Stored && credential is null)
            { return ApiProblems.Problem(409, "credential.unavailable", "凭证不可用或接收方不匹配，请检查绑定或输入临时凭证。"); }
            return TypedResults.Ok(await probe.RunAsync(settings, credential, context.RequestAborted));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["请检查 HTTP 地址、超时、非秘密请求头和认证参数。"] }); }
        catch (HttpConnectionUnavailableException)
        { return ApiProblems.Problem(409, "connection.inUse", "当前会话配置不匹配、正在释放或连接数达到上限，未建立第二个会话。"); }
    }

    private static HttpCredential Transient(HttpTestCredential value, DateTimeOffset now)
    {
        if (value.Kind == HttpCredentialKind.ClientCertificate)
        {
            if (value.Certificate is null || value.Secret is not null || value.Username is not null || value.Name is not null)
            { throw new ArgumentException("客户端证书须单独提交。"); }
            using var material = value.Certificate.Open(now);
            var data = material.ExportPkcs12();
            try
            { return new(new(value.Kind, null, null, "transient-test", "transient-test", material.Info.Subject), Convert.ToBase64String(data)); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        if (value.Certificate is not null || value.Secret is null)
        { throw new ArgumentException("认证参数无效。"); }
        return new(new(value.Kind, value.Username, value.Name, "transient-test", "transient-test"), value.Secret);
    }
}
