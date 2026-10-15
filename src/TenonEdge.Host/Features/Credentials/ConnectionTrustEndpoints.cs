using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Credentials;

internal static class ConnectionTrustEndpoints
{
    public static RouteGroupBuilder MapConnectionTrustEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/tls-trust/inspect", Inspect).WithTags("Connections")
            .WithMetadata(new RequestBodyLimit(64 * 1024)).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("inspectConnectionTrustCertificate").WithSummary("解析单个 CA 公钥证书供管理员核对，不保存或建立网络连接")
            .ProducesProblem(400).ProducesProblem(413);
        var group = api.MapGroup("/connections/{connectionId:long}").WithTags("Connections")
            .WithMetadata(new RequestBodyLimit(64 * 1024));
        group.MapGet("/tls-trust", Get).RequireAuthorization(AuthorizationPolicies.Configurator).WithName("getConnectionTlsTrust")
            .WithSummary("读取连接级服务器信任的元数据，不返回证书内容或任何私钥");
        group.MapPut("/tls-trust", Put).RequireAuthorization(AuthorizationPolicies.Administrator).WithName("writeConnectionTlsTrust")
            .WithSummary("管理员核对指纹后保存连接级固定服务器证书或 CA 信任")
            .WithDescription("立即影响后续连接操作，不应用草稿；保留名称/时间/用途校验，系统/连接信任均离线建链，不提供忽略错误。信任版本变化后重建连接；不撤销已经在途的请求。")
            .ProducesProblem(409).ProducesProblem(413);
        group.MapPost("/tls-trust/clear", Clear).RequireAuthorization(AuthorizationPolicies.Administrator).WithName("clearConnectionTlsTrust")
            .WithSummary("确认清除未被保留运行版本引用的连接信任，恢复系统信任").ProducesProblem(409);
        return api;
    }

    private static Results<Ok<ServerCertificateInfo>, ProblemHttpResult> Inspect(ConnectionTrustInspectRequest request,
        TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var info = ServerCertificateTrust.Validate(new(TlsTrustMode.CertificateAuthority, request.CertificateDerBase64, "inspection"));
            if (info.NotBefore > time.GetUtcNow() || info.NotAfter < time.GetUtcNow())
            { throw new ArgumentException("CA 证书不在有效期内。"); }
            return TypedResults.Ok(info);
        }
        catch (Exception exception) when (IsExpected(exception)) { return Failure(exception); }
    }

    private static Results<Ok<ConnectionTrustMetadata>, NotFound> Get(long connectionId, ConnectionTrustStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return store.Read(connectionId) is { } value ? TypedResults.Ok(value) : TypedResults.NotFound();
    }

    private static Results<Ok<ConnectionTrustMetadata>, ProblemHttpResult> Put(long connectionId, ConnectionTrustRequest request,
        ConnectionTrustStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Write(connectionId, request, ManagementActor.Capture(context))); }
        catch (Exception exception) when (IsExpected(exception)) { return Failure(exception); }
    }

    private static Results<NoContent, ProblemHttpResult> Clear(long connectionId, ConnectionTrustClearRequest request,
        ConnectionTrustStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { store.Clear(connectionId, request, ManagementActor.Capture(context)); return TypedResults.NoContent(); }
        catch (Exception exception) when (IsExpected(exception)) { return Failure(exception); }
    }

    private static bool IsExpected(Exception exception) => exception is CredentialWriteException or ApiTokenException or ArgumentException or FormatException or CryptographicException;
    private static ProblemHttpResult Failure(Exception exception) => exception switch
    {
        CredentialWriteException conflict => ApiProblems.Problem(409, conflict.Code, "连接信任未更改，请重新核对草稿、接收方和信任版本。"),
        ApiTokenException actor => ApiProblems.Problem(actor.Status, actor.Code, "管理员身份已变化，请重新认证。"),
        _ => ApiProblems.Problem(400, "trust.invalid", "请提供单个有效的 DER 公钥证书，核对信任模式、指纹和确认声明。"),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConnectionTrustInspectRequest(string CertificateDerBase64)
{
    public override string ToString() => "CA inspection (certificate omitted)";
}
