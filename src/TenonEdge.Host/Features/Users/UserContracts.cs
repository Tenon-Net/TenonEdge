using System.Security.Cryptography;
using System.Text.Json;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Users;

internal sealed record UserCreateRequest(string? AccountName, string? DisplayName, UserRole? Role, string? TemporaryPassword, bool IsEnabled = true)
{
    public override string ToString() => "User creation (password omitted)";
}
internal sealed record UserUpdateRequest(string? ExpectedVersion, string? DisplayName, UserRole? Role, bool IsEnabled, bool Confirmed, string? ExpectedTokenVersion = null, bool? RevokeTokens = null);
internal sealed record UserCommandRequest(string? ExpectedVersion, bool Confirmed, string? ExpectedTokenVersion = null, bool? RevokeTokens = null);
internal sealed record UserPasswordResetRequest(string? ExpectedVersion, bool Confirmed, string? TemporaryPassword)
{
    public override string ToString() => "User password reset (password omitted)";
}
internal sealed record UserPage(UserSummary[] Items, long? NextCursor);
internal sealed record UserMutationResult(UserSummary? User);
internal enum UserOperation { Update, ResetPassword, Unlock, EndSessions, Delete }

/// <summary>管理元数据，永不返回密码/哈希/安全戳。Version是字段快照的不可逆摘要，不是会话凭据或可重放操作票据。</summary>
internal sealed record UserSummary(long Id, string AccountName, string? DisplayName, UserRole Role, bool IsEnabled,
    bool MustChangePassword, DateTimeOffset? LockedUntil, DateTimeOffset? LastLoginAt, string? LastLoginIp, string Version)
{
    internal static UserSummary From(UserAccountRecord user, DateTimeOffset now) => new(user.Id, user.AccountName, user.DisplayName,
        Enum.Parse<UserRole>(user.Role), user.IsEnabled, user.MustChangePassword, AccountLockouts.LockedUntil(user, now),
        user.LastLoginAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null, user.LastLoginIp, VersionOf(user));

    // 登录时间/IP不改变管理版本；安全戳绑定密码/会话代次，锁定字段避免过时的解锁确认释放新一轮锁定。
    internal static string VersionOf(UserAccountRecord user) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { user.Id, user.DisplayName, user.Role, user.IsEnabled, user.SecurityStamp, user.MustChangePassword, user.LockoutEnd, user.FailedLoginCount })));
}

internal sealed class UserManagementException(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
