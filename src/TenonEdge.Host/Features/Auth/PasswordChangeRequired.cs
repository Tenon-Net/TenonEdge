using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 须先修改密码（总设计 §3.3：管理员设置的临时密码，用户首次登录必须先修改）：会话处于该状态时，
/// 除查看会话、登出和修改密码以外的管理 API 一律返回 403，code 为 auth.passwordChangeRequired。
/// </summary>
internal sealed class PasswordChangeRequiredFilter : IEndpointFilter
{
    public const string Code = "auth.passwordChangeRequired";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var mustChange = httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.FindFirst(SessionClaims.MustChangePassword)?.Value == "true";
        if (mustChange && httpContext.GetEndpoint()?.Metadata.GetMetadata<AllowedWhenPasswordChangeRequired>() is null)
        {
            return ValueTask.FromResult<object?>(ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                Code,
                "须先修改密码。",
                "当前账号使用的是临时密码，请先修改密码再继续操作。"));
        }

        return next(context);
    }
}

/// <summary>端点元数据：会话须先修改密码时仍然允许访问。</summary>
internal sealed class AllowedWhenPasswordChangeRequired;
