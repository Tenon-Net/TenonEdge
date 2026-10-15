using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>仅捕获已经认证的标识；敏感写操作仍在自己的短事务内复核，令牌不是创建者的会话。</summary>
internal sealed record ManagementActor(long? UserId, string? SecurityStamp, string? SourceIp, long? TokenId = null)
{
    public static ManagementActor Capture(HttpContext context)
    {
        var user = context.User;
        if (ManagementIdentity.IsToken(user) && long.TryParse(user.FindFirstValue(ApiTokenAuthentication.IdClaim), NumberStyles.None,
            CultureInfo.InvariantCulture, out var tokenId))
        { return new(null, null, SourceAddress.Of(context), tokenId); }
        if (user.Identity is { IsAuthenticated: true, AuthenticationType: SessionAuthentication.Scheme } &&
            long.TryParse(user.FindFirstValue(SessionClaims.UserId), NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
            user.FindFirstValue(SessionClaims.SecurityStamp) is { } stamp)
        { return new(id, stamp, SourceAddress.Of(context)); }
        throw new ApiTokenException(401, "auth.actorChanged");
    }

    public (AuditAccount Audit, long CreatorUserId) RequireAdministrator(SqlSugarClient client, long now)
    {
        if (TokenId is { } tokenId)
        {
            var token = client.Queryable<ApiTokenRecord>().InSingle(tokenId);
            if (!ApiTokenStore.IsUsable(token, now) || token!.Role != nameof(UserRole.Administrator))
            { throw new ApiTokenException(401, "auth.actorChanged"); }
            return (new(token.CreatorAccountName, token.Name), token.CreatorUserId);
        }
        var user = UserId is { } id ? client.Queryable<UserAccountRecord>().InSingle(id) : null;
        if (user is null || !user.IsEnabled || user.MustChangePassword || user.Role != nameof(UserRole.Administrator) || user.SecurityStamp != SecurityStamp)
        { throw new ApiTokenException(401, "auth.actorChanged"); }
        return (new(user.AccountName), user.Id);
    }

    public AuditAccount RequireConfigurator(SqlSugarClient client, long now)
    {
        if (TokenId is { } tokenId)
        {
            var token = client.Queryable<ApiTokenRecord>().InSingle(tokenId);
            if (!ApiTokenStore.IsUsable(token, now) || token!.Role is not (nameof(UserRole.Administrator) or nameof(UserRole.Configurator)))
            { throw new ApiTokenException(401, "auth.actorChanged"); }
            return new(token.CreatorAccountName, token.Name);
        }
        var user = UserId is { } id ? client.Queryable<UserAccountRecord>().InSingle(id) : null;
        if (user is null || !user.IsEnabled || user.MustChangePassword || user.Role is not (nameof(UserRole.Administrator) or nameof(UserRole.Configurator)) || user.SecurityStamp != SecurityStamp)
        { throw new ApiTokenException(401, "auth.actorChanged"); }
        return new(user.AccountName);
    }
}

internal static class ManagementIdentity
{
    public static bool IsToken(ClaimsPrincipal user) => user.Identity is { IsAuthenticated: true, AuthenticationType: ApiTokenAuthentication.Scheme };
    public static AuditAccount Audit(ClaimsPrincipal user) => new(user.FindFirstValue(SessionClaims.AccountName),
        IsToken(user) ? user.FindFirstValue(ApiTokenAuthentication.NameClaim) : null);
    public static string? BudgetKey(ClaimsPrincipal user) => IsToken(user)
        ? TokenKey(user) : user.FindFirstValue(SessionClaims.CsrfToken) is { Length: > 0 } csrf ? Hash(csrf) : null;
    public static string? OwnerKey(ClaimsPrincipal user) => IsToken(user) ? TokenKey(user)
        : user.FindFirstValue(SessionClaims.CsrfToken) is { Length: > 0 } csrf && user.FindFirstValue(SessionClaims.UserId) is { } id ? Hash(id + ":" + csrf) : null;
    private static string? TokenKey(ClaimsPrincipal user) => user.FindFirstValue(ApiTokenAuthentication.IdClaim) is { } id ? "token:" + Hash(id) : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
