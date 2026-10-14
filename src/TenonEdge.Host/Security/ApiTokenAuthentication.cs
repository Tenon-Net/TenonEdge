using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Tokens;

namespace TenonEdge.Host.Security;

internal static class ApiTokenAuthentication
{
    public const string Scheme = "TenonEdge.ApiToken";
    public const string ManagementScheme = "TenonEdge.Management";
    public const string IdClaim = "tenonedge:api-token-id";
    public const string NameClaim = "tenonedge:api-token-name";
    public static IServiceCollection AddApiTokens(this IServiceCollection services)
    {
        services.AddSingleton<ApiTokenStore>();
        services.AddSingleton<ApiTokenFailures>();
        services.AddAuthentication(ManagementScheme)
            .AddPolicyScheme(ManagementScheme, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("Authorization") ? Scheme : SessionAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(Scheme, _ => { });
        return services;
    }
    // 在登录限流/匿名端点之前拒绝Authorization请求；即使带有效Cookie也绝不退回会话。
    // 先按可信协议判定拒绝HTTP，不做HTTPS重定向，不验证长期秘密、不签发Cookie。
    public static WebApplication UseApiTokenRequests(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.ContainsKey("Authorization"))
            { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"TenonEdge\"";
            if (!RequestProtocol.IsHttps(context))
            { await Problem(403, "token.httpsRequired").ExecuteAsync(context); return; }
            if (context.Request.Path.StartsWithSegments(ManagementApi.RoutePrefix + "/setup") ||
                context.Request.Path.StartsWithSegments(ManagementApi.RoutePrefix + "/auth"))
            { await Problem(403, "token.sessionEndpointNotAllowed").ExecuteAsync(context); return; }
            var result = await context.AuthenticateAsync(Scheme);
            if (!result.Succeeded)
            {
                var error = result.Failure as ApiTokenException;
                await Problem(error?.Status ?? 401, error?.Code ?? "token.invalid").ExecuteAsync(context);
                return;
            }
            context.User = result.Principal!;
            await next(context);
        });
        return app;
    }
    private static ProblemHttpResult Problem(int status, string code) => ApiProblems.Problem(status, code,
        "令牌请求被拒绝。请使用可信HTTPS连接、有效令牌及适用的管理接口；不会回退到Cookie会话。");
}

internal sealed class ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ApiTokenStore tokens, ApiTokenFailures failures)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ip = SourceAddress.Of(Context) ?? "unknown";
        using var attempt = failures.TryEnter(ip, out var delay);
        if (attempt is null)
        {
            Response.Headers.RetryAfter = delay.ToString(CultureInfo.InvariantCulture);
            return Failure(429, "token.rateLimited");
        }
        var header = Request.Headers.Authorization;
        var text = header.Count == 1 ? header[0] : null;
        if (!RequestProtocol.IsHttps(Context) || text is null || text.Length > 71 ||
            !text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !ApiTokenSecrets.TryId(text[7..], out _))
        { attempt.Failed = true; return Failure(401, "token.invalid"); }
        ApiTokenRecord? row;
        try
        { row = tokens.Authenticate(text[7..]); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { return Failure(503, "token.storageUnavailable"); }
        if (row is null)
        { attempt.Failed = true; return Failure(401, "token.invalid"); }
        var identity = new ClaimsIdentity([
            new(ApiTokenAuthentication.IdClaim, row.Id.ToString(CultureInfo.InvariantCulture)),
            new(ApiTokenAuthentication.NameClaim, row.Name),
            new(SessionClaims.AccountName, row.CreatorAccountName),
            new(SessionClaims.Role, row.Role)], ApiTokenAuthentication.Scheme, SessionClaims.AccountName, SessionClaims.Role);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), ApiTokenAuthentication.Scheme)));
    }
    private static Task<AuthenticateResult> Failure(int status, string code) => Task.FromResult(AuthenticateResult.Fail(new ApiTokenException(status, code)));
}
