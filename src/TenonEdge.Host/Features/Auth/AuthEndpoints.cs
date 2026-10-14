using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>登录、登出、当前会话与修改密码（auth 分组，总设计 §10.1）。登录是匿名例外，其余需要登录。</summary>
internal static class AuthEndpoints
{
    public const string InvalidCredentialsCode = "auth.invalidCredentials";

    public const string LockedOutCode = "auth.lockedOut";

    public const string SessionEndedCode = "auth.sessionEnded";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .WithTags("Auth")
            .WithName("login")
            .WithSummary("登录")
            .WithDescription("账号或密码错误时返回 401，不区分账号是否存在；连续失败达到上限后返回 429 并带 Retry-After。")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(LoginRateLimiting.Policy);

        api.MapPost("/auth/logout", LogoutAsync)
            .WithTags("Auth")
            .WithName("logout")
            .WithSummary("登出")
            .WithMetadata(new AllowedWhenPasswordChangeRequired());

        api.MapGet("/auth/session", GetSession)
            .WithTags("Auth")
            .WithName("getSession")
            .WithSummary("当前会话")
            .WithDescription("返回当前账号、角色、是否须先修改密码，以及写操作须回传的防 CSRF 令牌。")
            .WithMetadata(new AllowedWhenPasswordChangeRequired());

        api.MapPost("/auth/password", ChangePasswordAsync)
            .WithTags("Auth")
            .WithName("changePassword")
            .WithSummary("修改自己的密码")
            .WithDescription(
                "须输入原密码。成功后该账号的其他会话失效，当前会话重新签发。" +
                "原密码连续错误 5 次时结束该账号的全部会话（包括当前会话），返回 401；登录锁定不影响已登录的用户修改密码。" +
                "与登录共用按来源地址的限流，超出时返回 429。")
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(LoginRateLimiting.Policy)
            .WithMetadata(new AllowedWhenPasswordChangeRequired());

        return api;
    }

    private static async Task<Results<Ok<SessionResponse>, ValidationProblem, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        LoginService login,
        TimeProvider time,
        HttpContext context)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(request.AccountName))
        {
            errors["accountName"] = ["请输入账号。"];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors["password"] = ["请输入密码。"];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        switch (await login.LoginAsync(request.AccountName!, request.Password!, SourceAddress.Of(context), context.RequestAborted))
        {
            case LoginOutcome.Succeeded succeeded:
                var csrfToken = await SessionAuthentication.SignInAsync(context, succeeded.User, succeeded.PreviousLoginAt, succeeded.PreviousLoginIp);
                return TypedResults.Ok(SessionResponse.From(succeeded.User, csrfToken, succeeded.PreviousLoginAt, succeeded.PreviousLoginIp));
            case LoginOutcome.LockedOut lockedOut:
                return LockedOut(context, time, lockedOut.Until);
            default:
                return ApiProblems.Problem(StatusCodes.Status401Unauthorized, InvalidCredentialsCode, "账号或密码错误。");
        }
    }

    private static ProblemHttpResult LockedOut(HttpContext context, TimeProvider time, DateTimeOffset until)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return ApiProblems.Problem(StatusCodes.Status429TooManyRequests, LockedOutCode, "尝试次数过多，请 15 分钟后再试。");
    }

    private static async Task<NoContent> LogoutAsync(AuditTrail audit, HttpContext context)
    {
        var accountName = context.User.FindFirst(SessionClaims.AccountName)?.Value;
        await SessionAuthentication.SignOutAsync(context);
        audit.AppendOrLog(new AuditEntry(AuditActions.Logout, AuditResults.Success, accountName, SourceAddress.Of(context)));
        return TypedResults.NoContent();
    }

    private static Results<Ok<SessionResponse>, ProblemHttpResult> GetSession(ConfigDatabase database, HttpContext context)
    {
        var user = CurrentUser(database, context);
        var csrfToken = context.User.FindFirst(SessionClaims.CsrfToken)?.Value;
        var previous = SessionAuthentication.PreviousLogin(context.User);
        return user is null || csrfToken is null
            ? SessionEnded()
            : TypedResults.Ok(SessionResponse.From(user, csrfToken, previous.At, previous.Ip));
    }

    private static async Task<Results<Ok<SessionResponse>, ValidationProblem, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request,
        AccountService accounts,
        HttpContext context)
    {
        if (!TryGetUserId(context, out var userId) || context.User.FindFirst(SessionClaims.SecurityStamp)?.Value is not { } stamp)
        {
            return SessionEnded();
        }

        var outcome = await accounts.ChangePasswordAsync(
            userId,
            stamp,
            request.CurrentPassword,
            request.NewPassword,
            SourceAddress.Of(context),
            context.RequestAborted);
        switch (outcome)
        {
            case PasswordChangeOutcome.Changed changed:
                var previous = SessionAuthentication.PreviousLogin(context.User);
                var csrfToken = await SessionAuthentication.SignInAsync(context, changed.User, previous.At, previous.Ip);
                return TypedResults.Ok(SessionResponse.From(changed.User, csrfToken, previous.At, previous.Ip));
            case PasswordChangeOutcome.Invalid invalid:
                return ApiProblems.Validation(invalid.Errors);
            case PasswordChangeOutcome.SessionsEnded:
                await SessionAuthentication.SignOutAsync(context);
                return ApiProblems.Problem(
                    StatusCodes.Status401Unauthorized,
                    SessionEndedCode,
                    "原密码连续错误次数过多，该账号的全部会话已结束，请重新登录。");
            default:
                return SessionEnded();
        }
    }

    private static UserAccountRecord? CurrentUser(ConfigDatabase database, HttpContext context) =>
        TryGetUserId(context, out var userId)
            ? database.Client.Queryable<UserAccountRecord>().InSingle(userId) is { IsEnabled: true } user ? user : null
            : null;

    private static bool TryGetUserId(HttpContext context, out long userId) =>
        long.TryParse(context.User.FindFirst(SessionClaims.UserId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out userId);

    private static ProblemHttpResult SessionEnded() =>
        ApiProblems.Problem(StatusCodes.Status401Unauthorized, SessionEndedCode, "会话已失效，请重新登录。");
}
