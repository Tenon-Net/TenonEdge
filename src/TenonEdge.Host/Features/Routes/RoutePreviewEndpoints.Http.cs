using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteHttpPreviewRequest(string ExpectedVersion, RouteGraph Graph, long SourceId);

internal static partial class RoutePreviewEndpoints
{
    private static void MapHttpSamplePreview(RouteGroupBuilder api) =>
        api.MapPost("/routes/preview/http", PreviewHttp).WithTags("Routes").WithName("previewRouteHttpSample")
            .WithSummary("显式读取图内 HTTP 来源一次并预览当前路线；不访问输出、不保存、不入队")
            .WithDescription("复用串行 HTTP 会话、凭证与出站策略。读取最多 1 MiB 完整 JSON 并保精度解码，不受设备诊断展示预算裁剪；脱敏、输入超限或读取失败返回 422。整次 30 秒。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(422).ProducesProblem(429).ProducesProblem(504);

    private static async Task<Results<Ok<RoutePreviewResponse>, ValidationProblem, ProblemHttpResult>> PreviewHttp(
        RouteHttpPreviewRequest request, RouteDraftStore drafts, HttpReadPreview reader,
        PreviewOperationBudget budget, IClockSyncStatus clock, TimeProvider time, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        try
        {
            // One versioned detached snapshot; never keep a database transaction open while reading.
            var snapshot = drafts.ReadGraphCompilation(request.ExpectedVersion, request.Graph, operation.Token);
            if (!snapshot.Inputs.TryGetValue(request.SourceId, out var input) ||
                !snapshot.Devices.TryGetValue(input.DeviceId, out var device))
            { throw new ArgumentException("来源不在图内。"); }
            if (input.Kind != DeviceKind.Http)
            { throw new ArgumentException("来源不是 HTTP。"); }
            var connection = device.ConnectionId is { } id ? snapshot.Connections.GetValueOrDefault(id) : null;
            var compiled = RouteSourceCompiler.Compile(input, device, connection, false);
            var state = clock.State;
            var captured = await reader.ReadSampleAsync(HttpInputSettings.Parse(compiled.Unit.Configuration),
                input.Key, device.Key, state, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (captured.Message is null)
            {
                return ApiProblems.Problem(422, "preview.http." + captured.Code, "未取得完整且可用于路线预览的 HTTP 样本；未执行输出、保存或入队。");
            }
            var result = RouteSamplePreview.Run(snapshot, request.SourceId, captured.Message, operation.Token);
            return TypedResults.Ok(new RoutePreviewResponse(snapshot.Version, true, captured.InvalidUpstreamTime, result, []));
        }
        catch (RouteTopologyException error)
        { return TypedResults.Ok(new RoutePreviewResponse(request.ExpectedVersion, false, false, null, [error.Issue])); }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(409, error.Code, "草稿已变化，未保存或应用本地图；请刷新核对。"); }
        catch (HttpConnectionUnavailableException)
        { return ApiProblems.Problem(409, "connection.inUse", "HTTP 共享参数不匹配、连接正在释放或容量不足；未创建第二会话。"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "preview.deadline", "现场读取与预览超过 30 秒，未保存、发送或入队。"); }
        catch (Exception error) when (error is ArgumentException or JsonException or InputMessageSizeException or RuntimeUnitBuildException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查当前图、HTTP 来源与草稿版本。"] }); }
    }
}
