namespace TenonEdge.Host.Bootstrap;

/// <summary>只提供 WebRoot 中的公开构建产物；API 和已有端点永不进入 SPA 回退。</summary>
internal static class WebAssets
{
    public static IApplicationBuilder UseTenonEdgeWebAssets(this WebApplication app)
    {
        app.UseWhen(context => context.GetEndpoint() is null && !IsReserved(context.Request.Path), branch =>
        {
            var options = new StaticFileOptions
            {
                FileProvider = app.Environment.WebRootFileProvider,
                OnPrepareResponse = context =>
                {
                    // HTML 每次重新获取，避免升级后引用已经移除的脚本；其他资源保留框架的条件请求。
                    if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Context.Response.Headers.CacheControl = "no-store";
                    }

                    context.Context.Response.Headers.XContentTypeOptions = "nosniff";
                },
            };
            branch.UseStaticFiles(options);
            branch.Use(async (context, next) =>
            {
                var request = context.Request;
                // 仅浏览器文档导航回退。缺失的资源、写请求或 JSON 请求保持原来的错误语义。
                var documentRequest = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                    && !request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                    && !Path.HasExtension(request.Path.Value)
                    && request.GetTypedHeaders().Accept?.Any(value =>
                        value.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                        && (value.Quality ?? 1) > 0) == true;
                var original = request.Path;
                if (documentRequest)
                {
                    request.Path = "/index.html";
                }

                try
                {
                    await next(context);
                }
                finally
                {
                    request.Path = original;
                }
            });
            branch.UseStaticFiles(options);
        });

        return app;
    }

    private static bool IsReserved(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/readyz", StringComparison.OrdinalIgnoreCase);
}
