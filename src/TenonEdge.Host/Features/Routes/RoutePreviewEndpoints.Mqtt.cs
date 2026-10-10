using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteMqttPreviewRequest(string ExpectedVersion, RouteGraph Graph, long SourceId);

internal static partial class RoutePreviewEndpoints
{
    private static void MapMqttSamplePreview(RouteGroupBuilder api) =>
        api.MapPost("/routes/preview/mqtt", PreviewMqtt).WithTags("Routes").WithName("previewRouteMqttSample")
            .WithSummary("显式抓取图内 MQTT 来源一条消息并预览路线；不发布、不保存、不入队")
            .WithDescription("复用随机 ClientId/CleanSession 诊断订阅，不影响正式订阅。标记保留/重复消息；完整且未脱敏才计算，采用正式解码完整消息，不受诊断展示裁剪；失败、脱敏或协议超限返回 422。整次 30 秒。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(422).ProducesProblem(429).ProducesProblem(504);

    private static async Task<Results<Ok<RoutePreviewResponse>, ValidationProblem, ProblemHttpResult>> PreviewMqtt(
        RouteMqttPreviewRequest request, RouteDraftStore drafts, MqttReadPreview reader,
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
            if (input.Kind != DeviceKind.Mqtt)
            { throw new ArgumentException("来源不是 MQTT。"); }
            var connection = device.ConnectionId is { } id ? snapshot.Connections.GetValueOrDefault(id) : null;
            var compiled = RouteSourceCompiler.Compile(input, device, connection, connection is not null && snapshot.MqttCredentials.Contains(connection.Id));
            var state = clock.State;
            var received = await reader.ReadSampleAsync(MqttInputSettings.Parse(compiled.Unit.Configuration),
                input.Key, device.Key, state, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (received.Message is null)
            {
                return ApiProblems.Problem(422, "preview.mqtt." + received.Code, "未取得完整且可用于路线预览的 MQTT 样本；未执行输出、保存或入队。");
            }
            var result = RouteSamplePreview.Run(snapshot, request.SourceId, received.Message, operation.Token);
            return TypedResults.Ok(new RoutePreviewResponse(snapshot.Version, true, received.InvalidUpstreamTime, result, [],
                new(received.Retained, received.Duplicate, received.Qos)));
        }
        catch (RouteTopologyException error)
        { return TypedResults.Ok(new RoutePreviewResponse(request.ExpectedVersion, false, false, null, [error.Issue])); }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(409, error.Code, "草稿已变化，未保存或应用本地图；请刷新核对。"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "preview.deadline", "现场读取与预览超过 30 秒，未保存、发送或入队。"); }
        catch (Exception error) when (error is ArgumentException or JsonException or InputMessageSizeException or RuntimeUnitBuildException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查当前图、MQTT 来源与草稿版本。"] }); }
    }
}
