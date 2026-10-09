using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Inputs;

internal static class InputDraftEndpoints
{
    public static RouteGroupBuilder MapInputDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/devices/{deviceId:long}/inputs").WithTags("Inputs");
        group.MapGet("", Read).WithName("getInputDrafts").WithSummary("查看设备来源草稿和规范化点表");
        group.MapPost("", (long deviceId, InputDraftRequest request, InputDraftStore store, HttpContext context) =>
            Write(deviceId, null, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("createInputDraft")
            .WithSummary("新建 Modbus 来源草稿，不开始采集").WithMetadata(new RequestBodyLimit(16 * 1024 * 1024));
        group.MapPut("/{inputId:long}", (long deviceId, long inputId, InputDraftRequest request, InputDraftStore store, HttpContext context) =>
            Write(deviceId, inputId, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("updateInputDraft")
            .WithSummary("保存 Modbus 来源及零基偏移点表，不改变运行配置").WithMetadata(new RequestBodyLimit(16 * 1024 * 1024));
        group.MapDelete("/{inputId:long}", Delete).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("deleteInputDraft").WithSummary("删除未被保留运行版本引用的来源草稿");
        return api;
    }

    private static Results<Ok<InputDraftList>, ProblemHttpResult> Read(long deviceId, InputDraftStore store)
    {
        try
        { return TypedResults.Ok(store.Read(deviceId)); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<InputDraftResult>, ValidationProblem, ProblemHttpResult> Write(long deviceId, long? id,
        InputDraftRequest request, InputDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Write(deviceId, id, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["input"] = ["来源或点位参数无效，请检查 key、周期、地址、类型、字节序与位号。"] }); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<DraftVersion>, ProblemHttpResult> Delete(long deviceId, long inputId, string expectedVersion,
        InputDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Delete(deviceId, inputId, expectedVersion, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static ProblemHttpResult Failure(DraftWriteException exception) => ApiProblems.Problem(
        exception.Code.EndsWith(".notFound", StringComparison.Ordinal) ? 404 : 409, exception.Code,
        "草稿未修改，请核对版本、设备归属、设备内唯一 key 与引用关系。");
}
