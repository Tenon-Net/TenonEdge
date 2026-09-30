using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Security;

/// <summary>
/// 防 CSRF（总设计 §3.3、§10.3）：以 Cookie 会话发出的写操作（POST、PUT、PATCH、DELETE），须在请求头 X-CSRF-Token 中回传
/// 与会话绑定的令牌，否则返回 403，code 为 auth.csrfTokenInvalid。令牌在签发会话时生成，随会话按协议分开；
/// 比较使用固定时间算法。匿名接口（首次设置、登录）没有会话可以冒用，不做这项检查；API 令牌（T3-09）不走 Cookie，同样不检查。
/// </summary>
internal sealed class CsrfProtectionFilter : IEndpointFilter
{
    public const string HeaderName = "X-CSRF-Token";

    public const string Code = "auth.csrfTokenInvalid";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (HttpMethods.IsGet(httpContext.Request.Method)
            || HttpMethods.IsHead(httpContext.Request.Method)
            || HttpMethods.IsOptions(httpContext.Request.Method)
            || httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || httpContext.User.Identity is not { IsAuthenticated: true, AuthenticationType: SessionAuthentication.Scheme })
        {
            return next(context);
        }

        var expected = httpContext.User.FindFirst(SessionClaims.CsrfToken)?.Value;
        var provided = httpContext.Request.Headers[HeaderName].ToString();
        if (expected is null
            || provided.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided)))
        {
            return ValueTask.FromResult<object?>(ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                Code,
                "防 CSRF 令牌缺失或不正确。",
                $"写操作须在请求头 {HeaderName} 中回传登录时下发的令牌。"));
        }

        return next(context);
    }
}
