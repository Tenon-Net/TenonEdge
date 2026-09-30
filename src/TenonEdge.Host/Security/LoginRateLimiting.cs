using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Security;

/// <summary>
/// 登录请求的限流（总设计 §3.3、§8.4）：每个来源 IP 每分钟 10 次，按实际来源地址计算；只有来自受信任代理的请求才采用代理转发的客户端地址，
/// 伪造的转发头不改变分区。修改密码（须校验原密码）使用同一策略、同一计数。超限返回 429，带 Retry-After，code 为 auth.rateLimited。
/// API 令牌认证失败的限流另外计数（T3-09）。
/// </summary>
internal static class LoginRateLimiting
{
    public const string Policy = "login";

    public const int PermitLimit = 10;

    public const string Code = "auth.rateLimited";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                SourceAddress.Of(context) ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : Window;
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await ApiProblems.Problem(
                    StatusCodes.Status429TooManyRequests,
                    Code,
                    "登录或验证密码的请求过于频繁，请稍后再试。").ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }
}
