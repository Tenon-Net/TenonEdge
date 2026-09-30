namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 请求管道装配：统一错误响应、路由与授权。
/// </summary>
internal static class RequestPipeline
{
    public static WebApplication UseTenonEdgeRequestPipeline(this WebApplication app)
    {
        // 未处理异常统一转为 500 ProblemDetails，不向客户端返回异常类型、消息或堆栈。
        app.UseExceptionHandler();

        // 没有响应体的 4xx/5xx（未匹配的路由、不允许的方法、未认证等）补写 ProblemDetails。
        app.UseStatusCodePages();

        app.UseRouting();
        app.UseAuthorization();

        return app;
    }
}
