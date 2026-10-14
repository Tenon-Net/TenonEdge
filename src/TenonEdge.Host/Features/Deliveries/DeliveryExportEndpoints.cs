using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryExportEndpoints
{
    public static RouteGroupBuilder MapDeliveryExportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/deliveries/export").WithTags("Deliveries")
            .RequireAuthorization(AuthorizationPolicies.Administrator);
        group.MapPost("", Prepare).WithName("prepareDeliveryExport").WithSummary("核对整个失败／隔离筛选范围")
            .WithDescription("一台网关最多一份临时导出，有效期15分钟（含准备、生成和下载）。202只表示已接收；使用同一登录会话查询状态。条数不套用列表分页上限，确认前不生成正文文件。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("", Current).WithName("getDeliveryExport").WithSummary("恢复当前会话的导出状态，不重新生成")
            .ProducesProblem(403);
        group.MapPost("/{id:guid}/confirm", Confirm).WithName("confirmDeliveryExport").WithSummary("明确确认后生成完整私有JSON Lines文件")
            .WithDescription("须 confirmed=true。只有ready状态可下载；选择变化、空间不足或IO失败整份拒绝。生成记审计；响应丢失只查询状态，不重复确认。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/cancel", Cancel).WithName("cancelDeliveryExport").WithSummary("取消本会话的导出并清除暂存文件")
            .ProducesProblem(403).ProducesProblem(404);
        group.MapGet("/{id:guid}/download", Download).WithName("downloadDeliveryExport").WithSummary("下载已生成文件，结束后清除暂存")
            .WithDescription("管理员且必须是创建资源的登录会话；ID不是授权令牌。原生下载，不在管理API客户端中全量缓存。一次下载，失败后重新准备。HTTP写出不等于用户已保存。")
            .Produces(200, contentType: "application/x-ndjson").ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        return api;
    }

    private static Results<Accepted<DeliveryExportStatus>, ProblemHttpResult> Prepare(DeliveryExportFilter filter,
        DeliveryExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Accepted((string?)null, manager.Prepare(Actor(context), filter)); }
        catch (ArgumentException) { return Invalid(); }
        catch (DeliveryExportRequestException exception) { return Problem(exception); }
    }

    private static Results<Ok<DeliveryExportCurrent>, ProblemHttpResult> Current(DeliveryExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(new DeliveryExportCurrent(manager.GetCurrent(Actor(context).Owner))); }
        catch (DeliveryExportRequestException exception) { return Problem(exception); }
    }

    private static Results<Accepted<DeliveryExportStatus>, ProblemHttpResult> Confirm(Guid id, DeliveryExportConfirmation request,
        DeliveryExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!request.Confirmed)
        { return Invalid(); }
        try
        { return TypedResults.Accepted((string?)null, manager.Confirm(id, Actor(context).Owner)); }
        catch (DeliveryExportRequestException exception) { return Problem(exception); }
    }

    private static Results<Ok<DeliveryExportStatus>, ProblemHttpResult> Cancel(Guid id, DeliveryExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(manager.Cancel(id, Actor(context).Owner)); }
        catch (DeliveryExportRequestException exception) { return Problem(exception); }
    }

    private static async Task<IResult> Download(Guid id, DeliveryExportManager manager, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        DeliveryExportDownload download;
        try
        { download = manager.OpenDownload(id, Actor(context).Owner); }
        catch (DeliveryExportRequestException exception) { return Problem(exception); }
        using (download)
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            context.Response.ContentLength = download.Length;
            context.Response.Headers.ContentDisposition = $"attachment; filename=tenonedge-deliveries-{id:D}.jsonl";
            try
            { await download.CopyToAsync(context.Response.Body, token); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // 已开始的下载不能改成200包着错误JSON。终止响应，浏览器通过长度/连接中断识别不完整文件。
                context.Abort();
            }
        }
        return TypedResults.Empty;
    }

    private static DeliveryExportActor Actor(HttpContext context)
    {
        var owner = ManagementIdentity.OwnerKey(context.User);
        var account = ManagementIdentity.Audit(context.User);
        if (owner is null || account.Name is null)
        { throw new DeliveryExportRequestException(403, "delivery.exportSessionRequired"); }
        return new(owner, account, SourceAddress.Of(context));
    }

    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "delivery.invalidExportRequest", "请核对故障区域、筛选条件并明确确认导出。");
    private static ProblemHttpResult Problem(DeliveryExportRequestException exception) => ApiProblems.Problem(exception.StatusCode,
        exception.Code, "导出不可执行。请读取当前状态；资源只属于创建它的登录会话，响应丢失不要自动重放。");
}

internal sealed record DeliveryExportConfirmation(bool Confirmed);

internal sealed record DeliveryExportCurrent(DeliveryExportStatus? Export);
