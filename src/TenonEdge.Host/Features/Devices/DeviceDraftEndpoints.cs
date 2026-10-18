using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Devices;

internal static class DeviceDraftEndpoints
{
    public static RouteGroupBuilder MapDeviceDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/devices").WithTags("Devices");
        group.MapGet("", (DeviceDraftStore store) => TypedResults.Ok(store.Read()))
            .WithName("getDeviceDrafts").WithSummary("查看设备草稿及全局草稿版本，不代表运行状态");
        group.MapGet("/{deviceId:long}/deletion-review", ReviewDeletion).WithName("getDeviceDeletionReview")
            .WithSummary("核对删除设备草稿的路线及保留版本引用，不授权或执行删除")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("", (DeviceDraftRequest request, DeviceDraftStore store, HttpContext context) => Write(null, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("createDeviceDraft")
            .WithSummary("新建设备草稿，保存不改变运行配置").ProducesProblem(409).ProducesProblem(403);
        group.MapPut("/{deviceId:long}", (long deviceId, DeviceDraftRequest request, DeviceDraftStore store, HttpContext context) => Write(deviceId, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("updateDeviceDraft")
            .WithSummary("更新设备草稿，key 不可修改").ProducesProblem(409).ProducesProblem(404).ProducesProblem(403);
        group.MapPost("/{deviceId:long}/copy", Copy).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("copyDeviceDraft").WithSummary("原子复制设备及来源点位，副本默认停用，最多 20 份")
            .ProducesProblem(409).ProducesProblem(404).ProducesProblem(403);
        group.MapDelete("/{deviceId:long}", Delete).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("deleteDeviceDraft").WithSummary("删除未被引用的设备草稿")
            .ProducesProblem(409).ProducesProblem(404).ProducesProblem(403);
        return api;
    }

    private static Results<Ok<DeviceDraftResult>, ValidationProblem, ProblemHttpResult> Write(long? id,
        DeviceDraftRequest request, DeviceDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Write(id, request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["device"] = ["设备参数无效，请检查标识、名称、连接类型、UnitId 及地址写法。"] }); }
        catch (FormatException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["device"] = ["设备参数格式无效。"] }); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<DeviceDraftCopyResult>, ValidationProblem, ProblemHttpResult> Copy(long deviceId,
        DeviceDraftCopyRequest request, DeviceDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Copy(deviceId, request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["copy"] = ["复制数量须为 1—20，且须提供草稿版本。"] }); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<DraftVersion>, ProblemHttpResult> Delete(long deviceId, string expectedVersion,
        DeviceDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Delete(deviceId, expectedVersion, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<DeviceDeletionReview>, ProblemHttpResult> ReviewDeletion(long deviceId, string expectedVersion,
        DeviceDraftStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.ReviewDeletion(deviceId, expectedVersion)); }
        catch (ArgumentException) { return ApiProblems.Problem(400, "device.invalidDeletionReview", "设备编号或草稿版本无效。"); }
        catch (DraftWriteException exception) { return Failure(exception); }
        catch (RuntimePlanDocumentException)
        { return ApiProblems.Problem(409, "device.referencesUnavailable", "保留运行版本不可核对，不能据此判断设备可删除。"); }
    }

    private static ProblemHttpResult Failure(DraftWriteException exception) => ApiProblems.Problem(
        exception.Code == "device.notFound" ? 404 : 409, exception.Code, "草稿未修改，请核对版本、唯一标识和引用关系后重试。");
}
