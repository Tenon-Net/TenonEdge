using TenonEdge.Host.Security;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 请求管道装配：统一错误响应、请求协议判定与转发头、路由、限流、认证与授权。
/// </summary>
internal static class RequestPipeline
{
    public static WebApplication UseTenonEdgeRequestPipeline(this WebApplication app)
    {
        // 未处理异常统一转为 500 ProblemDetails，不向客户端返回异常类型、消息或堆栈。
        app.UseExceptionHandler();

        // 没有响应体的 4xx/5xx（未匹配的路由、不允许的方法、未认证等）补写 ProblemDetails。
        app.UseStatusCodePages();

        // 请求协议判定与受信任代理的转发头，须在认证之前：Cookie 名称与会话校验依赖判定结果。
        app.UseRequestSecurity();

        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }
}
