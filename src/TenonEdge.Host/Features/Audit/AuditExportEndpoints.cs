using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Audit;

internal static class AuditExportEndpoints
{
    public static RouteGroupBuilder MapAuditExportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/audit/export").WithTags("Audit").RequireAuthorization(AuthorizationPolicies.Administrator);
        group.MapPost("", Start).WithName("startAuditExport").WithSummary("确认导出整个审计筛选范围为CSV")
            .WithDescription("须confirmed=true。202仅表示接收；后台冻结编号上界，分短页生成完整文件并持久化导出审计后才可下载。单网关一份，有效期15分钟（含生成、等待与下载）。响应丢失只查询，不自动重放。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("", Current).WithName("getAuditExport").WithSummary("查询当前会话或Token拥有的导出状态").ProducesProblem(403);
        group.MapPost("/{id:guid}/cancel", Cancel).WithName("cancelAuditExport").WithSummary("取消自己的审计导出并清理文件")
            .ProducesProblem(403).ProducesProblem(404);
        group.MapGet("/{id:guid}/download", Download).WithName("downloadAuditExport").WithSummary("一次性原生下载完整审计CSV")
            .WithDescription("同一会话或Token且仍为管理员才可下载，ID不是授权凭证。UTF-8 BOM、CRLF、标准引号转义，危险表格公式前缀加单引号。仅生成时上界以内的完整筛选结果，不受列表分页限制。HTTP写出不表示用户已保存。")
            .Produces(200, contentType: "text/csv").ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        return api;
    }
    private static Results<Accepted<AuditExportStatus>, ProblemHttpResult> Start(AuditExportRequest request, AuditExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!request.Confirmed || request.Filter is null)
        { return Invalid(); }
        try
        { return TypedResults.Accepted((string?)null, manager.Start(Actor(context), request.Filter)); }
        catch (ArgumentException) { return Invalid(); }
        catch (AuditExportRequestException exception) { return Problem(exception); }
    }
    private static Results<Ok<AuditExportCurrent>, ProblemHttpResult> Current(AuditExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(new AuditExportCurrent(manager.GetCurrent(Actor(context).Owner))); }
        catch (AuditExportRequestException exception) { return Problem(exception); }
    }
    private static Results<Ok<AuditExportStatus>, ProblemHttpResult> Cancel(Guid id, AuditExportManager manager, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(manager.Cancel(id, Actor(context).Owner)); }
        catch (AuditExportRequestException exception) { return Problem(exception); }
    }
    private static async Task<IResult> Download(Guid id, AuditExportManager manager, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        ExportDownload download;
        try
        { download = manager.OpenDownload(id, Actor(context).Owner); }
        catch (AuditExportRequestException exception) { return Problem(exception); }
        using (download)
        {
            context.Response.ContentType = "text/csv; charset=utf-8";
            context.Response.ContentLength = download.Length;
            context.Response.Headers.ContentDisposition = $"attachment; filename=tenonedge-audit-{id:D}.csv";
            try
            { await download.CopyToAsync(context.Response.Body, token); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { context.Abort(); }
        }
        return TypedResults.Empty;
    }
    private static AuditExportActor Actor(HttpContext context)
    {
        var owner = ManagementIdentity.OwnerKey(context.User);
        var account = ManagementIdentity.Audit(context.User);
        if (owner is null || account.Name is null)
        { throw new AuditExportRequestException(403, "audit.exportIdentityRequired"); }
        return new(owner, account, SourceAddress.Of(context));
    }
    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "audit.invalidExportRequest", "请核对完整筛选条件并明确确认导出。");
    private static ProblemHttpResult Problem(AuditExportRequestException exception) => ApiProblems.Problem(exception.StatusCode,
        exception.Code, "审计导出不可执行。请读取当前状态；仅创建它的会话或Token可操作，响应丢失不要自动重放。");
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AuditExportRequest(AuditFilter? Filter, bool Confirmed);
internal sealed record AuditExportCurrent(AuditExportStatus? Export);
