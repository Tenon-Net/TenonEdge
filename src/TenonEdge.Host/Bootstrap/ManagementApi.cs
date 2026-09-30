using Microsoft.OpenApi;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 管理 API 根分组（总设计 §10.1）：前缀 <c>/api/edge-admin/v1</c>，默认要求认证。
/// 各功能在 Features 下实现端点并挂到本分组；匿名例外须逐项显式声明并有测试覆盖。
/// 端点须用 <c>WithName</c> 声明全局唯一、小驼峰的 operationId，并声明成功与失败响应。
/// </summary>
internal static class ManagementApi
{
    public const string RoutePrefix = "/api/edge-admin/v1";

    public const string DocumentName = "v1";

    /// <summary>OpenAPI 文档相对于 <see cref="RoutePrefix"/> 的路由。</summary>
    public const string DocumentRoutePattern = "/openapi/{documentName}.json";

    public static IServiceCollection AddManagementApiDocument(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            // 显式固定规范版本，框架默认值变化时不无意改变契约。
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info.Title = "TenonEdge 管理 API";
                document.Info.Version = DocumentName;
                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static RouteGroupBuilder MapManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(RoutePrefix)
            .RequireAuthorization()
            .AddEndpointFilter<CsrfProtectionFilter>()
            .AddEndpointFilter<PasswordChangeRequiredFilter>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var document = api.MapOpenApi(DocumentRoutePattern);

        // API 文档在登录后查看（功能需求 §12.4）。仅开发环境匿名开放，便于本地核对契约与生成客户端。
        if (endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            document.AllowAnonymous();
        }

        return api;
    }
}
