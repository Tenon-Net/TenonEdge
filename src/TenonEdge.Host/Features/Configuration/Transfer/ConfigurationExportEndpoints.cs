using System.Text.Json;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal static class ConfigurationExportEndpoints
{
    public static RouteGroupBuilder MapConfigurationExportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/configuration/export", Export)
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithTags("Configuration")
            .WithName("exportConfigurationDraft").WithSummary("导出当前已保存草稿：业务标识与非秘密配置，不含内部编号、凭证或运行状态")
            .WithDescription("JSON文件格式版本1，最多16MiB；可选expectedVersion固定所见草稿。账户仅有不可认证的比较摘要和需重新填写标记。不会应用配置或改变草稿。")
            .Produces<PortableConfiguration>(200, "application/json")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task Export(string? expectedVersion, ConfigurationExportStore store, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        IResult result;
        if (failure is not null)
        { result = failure; }
        else
        {
            try
            {
                var bytes = store.Export(expectedVersion, context.RequestAborted);
                result = TypedResults.File(bytes, "application/json", "tenonedge-configuration.json");
            }
            catch (DraftWriteException error)
            { result = ApiProblems.Problem(409, error.Code, "草稿已变化，请重新读取后导出。"); }
            catch (ConfigurationTransferException error)
            {
                result = ApiProblems.Problem(error.Code == "configuration.exportTooLarge" ? 413 : 409,
                    error.Code, "配置无法安全导出，请核对草稿引用、凭证元数据和文件大小。", error.Path);
            }
            catch (RuntimePlanDocumentException)
            { result = ApiProblems.Problem(413, "configuration.exportTooLarge", "完整草稿超过16MiB，未导出文件。"); }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
            { result = ApiProblems.Problem(400, "configuration.exportInvalid", "草稿格式或导出版本参数无效，未导出文件。"); }
        }
        // Keep the original shared budget lease until the bounded file has been
        // written (or the request aborted), not merely until its allocation.
        await result.ExecuteAsync(context);
    }
}
