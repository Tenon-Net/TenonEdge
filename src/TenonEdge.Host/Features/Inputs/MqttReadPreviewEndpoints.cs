using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MqttReadPreviewRequest(string ExpectedVersion, long? InputId = null, MqttSourceDraft? Source = null);

internal static class MqttReadPreviewEndpoints
{
    internal static RouteGroupBuilder MapMqttReadPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/{deviceId:long}/preview/mqtt", Read).WithTags("Inputs").WithName("previewMqttSource")
            .RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithSummary("使用已保存设备连接显式抓取 MQTT 来源的一条消息，不保存、应用或入发送队列")
            .WithDescription("Source 为未保存来源；否则须按 InputId 选择已保存来源。独立随机 ClientId/CleanSession 诊断订阅，不改变正式订阅或其确认；整次最多 30 秒，显示最多 64 KiB，标记保留消息。")
            .WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<MqttReadPreviewResult>, ValidationProblem, ProblemHttpResult>> Read(
        long deviceId, MqttReadPreviewRequest request, InputDraftStore drafts, MqttReadPreview preview,
        IClockSyncStatus clock, PreviewOperationBudget budget, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var settings = drafts.ReadMqttPreview(deviceId, request);
            return TypedResults.Ok(await preview.RunAsync(settings, clock.State, context.RequestAborted));
        }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查 MQTT 来源、Topic Filter、QoS、字段和时间配置。"] }); }
        catch (DraftWriteException exception)
        { return ApiProblems.Problem(exception.Code.EndsWith(".notFound", StringComparison.Ordinal) ? 404 : 409, exception.Code, "草稿已变化或来源不属于该设备，请刷新核对，未执行采样。"); }

    }
}
