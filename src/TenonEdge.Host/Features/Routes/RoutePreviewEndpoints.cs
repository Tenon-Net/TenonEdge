using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RoutePreviewRequest(string ExpectedVersion, RouteGraph Graph, long SourceId, string SampleJson);
internal sealed record RouteMqttSampleInfo(bool Retained, bool Duplicate, int? Qos);
internal sealed record RoutePreviewResponse(string Version, bool Previewed, bool? InvalidUpstreamTime, RouteSamplePreviewResult? Result, RouteTopologyIssue[] Issues, RouteMqttSampleInfo? Mqtt = null)
{
    public override string ToString() => "Route preview response (body omitted)";
}

internal static partial class RoutePreviewEndpoints
{
    public static RouteGroupBuilder MapRoutePreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/routes/preview", Preview).WithTags("Routes").WithName("previewRouteSample")
            .WithSummary("使用手工 JSON 样本预览未保存路线，不连接输入/输出、不保存、不入队")
            .WithDescription("MQTT/HTTP 按来源的 JSON 定位与时间解码；Modbus/模拟来源接受已解码字段对象，不做寄存器或字节序仿真。样本最多 64 KiB，整个响应最多 64 KiB，截断结果仅供显示。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(504);
        MapHttpSamplePreview(api);
        MapMqttSamplePreview(api);
        MapModbusSamplePreview(api);
        MapRecentSamplePreview(api);
        return api;
    }

    private static Results<Ok<RoutePreviewResponse>, ValidationProblem, ProblemHttpResult> Preview(RoutePreviewRequest request,
        RouteDraftStore drafts, PreviewOperationBudget budget, IClockSyncStatus clock, TimeProvider time, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        try
        {
            var snapshot = drafts.ReadGraphCompilation(request.ExpectedVersion, request.Graph, operation.Token);
            var sample = RoutePreviewSample.Decode(snapshot, request.SourceId, request.SampleJson, time.GetUtcNow(), clock.State, operation.Token);
            var result = RouteSamplePreview.Run(snapshot, request.SourceId, sample.Message, operation.Token);
            return TypedResults.Ok(new RoutePreviewResponse(snapshot.Version, true, sample.InvalidUpstreamTime, result, []));
        }
        catch (RouteTopologyException error)
        { return TypedResults.Ok(new RoutePreviewResponse(request.ExpectedVersion, false, false, null, [error.Issue])); }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(409, error.Code, "草稿已变化，请重新读取；本地样本和图未保存或应用。"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "preview.deadline", "预览超过 30 秒，未保存、发送或入队。"); }
        catch (Exception error) when (error is ArgumentException or JsonException or InputMessageSizeException or RuntimeUnitBuildException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查当前图、所选来源、版本和不超过 64 KiB 的手工 JSON 样本。"] }); }
    }
}
