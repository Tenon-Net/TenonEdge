using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>
/// 管理会话（总设计 §3.3、§10.3）：同源 Cookie 认证，HttpOnly、SameSite=Strict，空闲 2 小时滑动过期，不做刷新令牌。
/// <list type="bullet">
/// <item>HTTP 与 HTTPS 的 Cookie 分开命名：HTTPS 使用带 __Host- 前缀、带 Secure 的 Cookie，HTTP 使用另一个名称；
/// 服务端只读取与请求协议相符的那一个（<see cref="ProtocolCookieManager"/>）。</item>
/// <item>会话票据由 Data Protection 保护，写入用户 ID、账号、角色、安全戳、会话代次、签发时的协议、防 CSRF 令牌和“须修改密码”标记；
/// 每次请求由 <see cref="SessionSecurityState.ValidateAsync"/> 核对，写操作的防 CSRF 令牌由 <see cref="CsrfProtectionFilter"/> 核对。</item>
/// <item>未登录或无权时返回 401/403，不跳转到登录页面。</item>
/// </list>
/// </summary>
internal static class SessionAuthentication
{
    public const string Scheme = "TenonEdge.Session";

    public const string HttpsCookieName = "__Host-TenonEdge.Session";

    public const string HttpCookieName = "TenonEdge.Session";

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);

    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = HttpCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.Cookie.IsEssential = true;
            options.CookieManager = new ProtocolCookieManager();
            options.ExpireTimeSpan = IdleTimeout;
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = SessionSecurityState.ValidateAsync;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        return services;
    }

    /// <summary>为账号签发会话，返回写入会话的防 CSRF 令牌（客户端在写操作的请求头中回传）。</summary>
    public static async Task<string> SignInAsync(HttpContext context, UserAccountRecord user, DateTimeOffset? previousLoginAt = null, string? previousLoginIp = null)
    {
        var csrfToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var identity = new ClaimsIdentity(
            [
                new Claim(SessionClaims.UserId, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(SessionClaims.AccountName, user.AccountName),
                new Claim(SessionClaims.Role, user.Role),
                new Claim(SessionClaims.SecurityStamp, user.SecurityStamp),
                new Claim(SessionClaims.Protocol, RequestProtocol.Name(context)),
                new Claim(SessionClaims.CsrfToken, csrfToken),
                new Claim(SessionClaims.MustChangePassword, user.MustChangePassword ? "true" : "false"),
                new Claim(
                    SessionClaims.SessionGeneration,
                    context.RequestServices.GetRequiredService<SessionSecurityState>().Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            Scheme,
            SessionClaims.AccountName,
            SessionClaims.Role);
        if (previousLoginAt is { } at)
        { identity.AddClaim(new Claim(SessionClaims.PreviousLoginAt, at.ToString("O", CultureInfo.InvariantCulture))); }
        if (previousLoginIp is not null)
        { identity.AddClaim(new Claim(SessionClaims.PreviousLoginIp, previousLoginIp)); }
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return csrfToken;
    }

    // 绑定登录当时的历史，不能在后续请求读取数据库里已被本次/其他会话覆盖的 LastLogin。
    public static (DateTimeOffset? At, string? Ip) PreviousLogin(ClaimsPrincipal principal) => (
        DateTimeOffset.TryParseExact(principal.FindFirstValue(SessionClaims.PreviousLoginAt), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : null,
        principal.FindFirstValue(SessionClaims.PreviousLoginIp));

    public static Task SignOutAsync(HttpContext context) => context.SignOutAsync(Scheme);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>会话票据中的声明类型。</summary>
internal static class SessionClaims
{
    public const string UserId = ClaimTypes.NameIdentifier;

    public const string AccountName = ClaimTypes.Name;

    public const string Role = ClaimTypes.Role;

    public const string SecurityStamp = "tenonedge:security_stamp";

    public const string Protocol = "tenonedge:protocol";

    public const string CsrfToken = "tenonedge:csrf_token";

    public const string MustChangePassword = "tenonedge:must_change_password";

    public const string PreviousLoginAt = "tenonedge:previous_login_at";

    public const string PreviousLoginIp = "tenonedge:previous_login_ip";

    public const string SessionGeneration = "tenonedge:session_generation";
}

/// <summary>
/// 按请求协议选择会话 Cookie 的名称与 Secure 标志：HTTPS 请求只读写 __Host- 前缀的 Cookie，HTTP 请求只读写另一个名称。
/// 浏览器经 HTTP 无法设置或覆盖带 Secure 的 __Host- Cookie（S-15）。
/// </summary>
internal sealed class ProtocolCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, NameFor(context));

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        options.Secure = RequestProtocol.IsHttps(context);
        options.Domain = null;
        options.Path = "/";
        _inner.AppendResponseCookie(context, NameFor(context), value, options);
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
    {
        options.Secure = RequestProtocol.IsHttps(context);
        options.Domain = null;
        options.Path = "/";
        _inner.DeleteCookie(context, NameFor(context), options);
    }

    private static string NameFor(HttpContext context) =>
        RequestProtocol.IsHttps(context) ? SessionAuthentication.HttpsCookieName : SessionAuthentication.HttpCookieName;
}
