using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>首次设置状态（按字符串序列化）。</summary>
internal enum SetupState
{
    Uninitialized,
    Initialized,
    ResetPending,
}

/// <summary>匿名可查的首次设置状态：只有窗口开放时才给出关闭时间，以及重置待设置时等待设置新密码的账号。</summary>
internal sealed record SetupStatusResponse(SetupState State, bool WindowOpen, DateTimeOffset? WindowClosesAt, string? ResetAccountName);

/// <summary>首次设置：管理员账号、密码与可选的网关名称。重置待设置时账号固定为被重置的账号，可以省略。</summary>
internal sealed record CompleteSetupRequest(string? AccountName, string? Password, string? GatewayName);

internal sealed record LoginRequest(string? AccountName, string? Password);

internal sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>当前会话：账号、角色、是否须先修改密码，以及写操作须在请求头中回传的防 CSRF 令牌。</summary>
internal sealed record SessionResponse(string AccountName, string? DisplayName, UserRole Role, bool MustChangePassword, string CsrfToken,
    DateTimeOffset? PreviousLoginAt, string? PreviousLoginIp)
{
    public const string CsrfHeaderName = "X-CSRF-Token";

    public static SessionResponse From(UserAccountRecord user, string csrfToken, DateTimeOffset? previousLoginAt = null, string? previousLoginIp = null) => new(
        user.AccountName,
        user.DisplayName,
        Enum.Parse<UserRole>(user.Role),
        user.MustChangePassword,
        csrfToken,
        previousLoginAt,
        previousLoginIp);
}
