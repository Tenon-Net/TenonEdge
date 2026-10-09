using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HttpReadPreviewRequest(string ExpectedVersion, long? InputId = null, HttpSourceDraft? Source = null);

internal static class HttpReadPreviewEndpoints
{
    internal static RouteGroupBuilder MapHttpReadPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/{deviceId:long}/preview/http", Read).WithTags("Inputs").WithName("previewHttpDevice")
            .RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithSummary("使用已保存设备连接显式采样 HTTP 来源，不保存、应用或入发送队列")
            .WithDescription("Source 为未保存来源；否则按 InputId 或全部启用来源读取。整次共用 30 秒/64 KiB 显示容量，各来源仍受连接超时限制。截断或脱敏正文不可用于样本推断。")
            .WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<HttpDevicePreviewResult>, ValidationProblem, ProblemHttpResult>> Read(
        long deviceId, HttpReadPreviewRequest request, InputDraftStore drafts, HttpReadPreview preview,
        IClockSyncStatus clock, PreviewOperationBudget budget, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var sources = drafts.ReadHttpPreview(deviceId, request);
            return TypedResults.Ok(await preview.RunSourcesAsync(sources, clock.State, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查 HTTP 来源路径、非秘密请求头、字段和时间配置。"] }); }
        catch (DraftWriteException exception)
        { return ApiProblems.Problem(exception.Code.EndsWith(".notFound", StringComparison.Ordinal) ? 404 : 409, exception.Code, "草稿已变化或来源不属于该设备，请刷新核对，未执行采样。"); }
        catch (HttpConnectionUnavailableException)
        { return ApiProblems.Problem(409, "connection.inUse", "共享 HTTP 参数不匹配、正在释放或连接数达到上限，未建立第二会话。"); }
    }
}
