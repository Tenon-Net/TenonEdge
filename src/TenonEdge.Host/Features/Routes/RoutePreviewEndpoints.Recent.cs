using System.Globalization;
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
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteRecentMessagesRequest(string ExpectedVersion, RouteGraph Graph, long SourceId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteRecentPreviewRequest(string ExpectedVersion, RouteGraph Graph, long SourceId,
    string Sequence, string Revision, Guid SourceMessageId);
internal sealed record RouteRecentMessageInfo(string Sequence, string Revision, Guid SourceMessageId,
    DateTimeOffset CapturedAt, int OriginalBytes, int StoredBytes, bool Truncated, bool Executable);
internal sealed record RouteRecentMessagesResponse(string Version, long SourceId, bool Running,
    string? ActiveRevision, RouteRecentMessageInfo[] Records);

internal static partial class RoutePreviewEndpoints
{
    private static void MapRecentSamplePreview(RouteGroupBuilder api)
    {
        api.MapPost("/routes/preview/recent/messages", RecentMessages).WithTags("Routes").WithName("listRouteRecentMessages")
            .WithSummary("列出图内来源的最近运行输入记录元数据；不返回正文、不触发采集")
            .WithDescription("最多 100 条，按捕获顺序倒序。Executable 仅为列表时刻提示；执行时重新检查状态、版本、截断及淘汰。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(504);
        api.MapPost("/routes/preview/recent", PreviewRecent).WithTags("Routes").WithName("previewRouteRecentSample")
            .WithSummary("使用明确选中的完整运行输入记录预览当前图；不采集、不连接输出、不保存、不入队")
            .WithDescription("要求输入正在运行且记录仍在当前运行版本的缓存中；停止、退避、截断或淘汰返回 409，不替换成其他记录。沿用捕获时的解码字段与时间，不重解析来源；上游时间解析告警未知时 InvalidUpstreamTime 为 null，MQTT 标志不在当前缓存中。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(504);
    }

    private static Results<Ok<RouteRecentMessagesResponse>, ValidationProblem, ProblemHttpResult> RecentMessages(
        RouteRecentMessagesRequest request, RouteDraftStore drafts, RuntimeHost runtime,
        PreviewOperationBudget budget, TimeProvider time, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        try
        {
            var snapshot = drafts.ReadGraphCompilation(request.ExpectedVersion, request.Graph, operation.Token);
            var id = RecentSource(snapshot, request.SourceId);
            var status = runtime.GetStatus();
            var running = status.Units.Any(unit => unit.Id == id && unit.State == RuntimeUnitState.Running);
            var records = runtime.Messages.Read(id, includeBody: false).Select(record => new RouteRecentMessageInfo(
                record.Sequence.ToString(CultureInfo.InvariantCulture), record.Revision.ToString(CultureInfo.InvariantCulture),
                record.SourceMessageId, record.CapturedAt, record.OriginalBytes, record.StoredBytes, record.Truncated,
                running && record.Revision == status.ActiveRevision && !record.Truncated)).ToArray();
            operation.Token.ThrowIfCancellationRequested();
            return TypedResults.Ok(new RouteRecentMessagesResponse(snapshot.Version, request.SourceId, running,
                status.ActiveRevision?.ToString(CultureInfo.InvariantCulture), records));
        }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(409, error.Code, "草稿已变化，请刷新核对；未采集、保存或应用。"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "preview.deadline", "读取最近记录超过 30 秒。"); }
        catch (Exception error) when (error is ArgumentException or JsonException or RuntimeUnitBuildException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查当前图、来源与草稿版本。"] }); }
    }

    private static Results<Ok<RoutePreviewResponse>, ValidationProblem, ProblemHttpResult> PreviewRecent(
        RouteRecentPreviewRequest request, RouteDraftStore drafts, RuntimeHost runtime,
        PreviewOperationBudget budget, TimeProvider time, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        try
        {
            var snapshot = drafts.ReadGraphCompilation(request.ExpectedVersion, request.Graph, operation.Token);
            var id = RecentSource(snapshot, request.SourceId);
            if (!long.TryParse(request.Sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 ||
                !long.TryParse(request.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0 ||
                request.SourceMessageId == Guid.Empty)
            { throw new ArgumentException("记录身份无效。"); }
            operation.Token.ThrowIfCancellationRequested();
            if (!runtime.TryReadInputSample(id, sequence, revision, request.SourceMessageId, out var sample))
            { return ApiProblems.Problem(409, "preview.recent.unavailable", "所选记录已不可执行：请核对输入运行状态、版本、截断或淘汰；未替换样本或触发采集。"); }
            var result = RouteSamplePreview.Run(snapshot, request.SourceId, sample, operation.Token);
            return TypedResults.Ok(new RoutePreviewResponse(snapshot.Version, true, null, result, []));
        }
        catch (RouteTopologyException error)
        { return TypedResults.Ok(new RoutePreviewResponse(request.ExpectedVersion, false, false, null, [error.Issue])); }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(409, error.Code, "草稿已变化，未保存或应用本地图；请刷新核对。"); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "preview.deadline", "预览超过 30 秒，未保存、发送或入队。"); }
        catch (Exception error) when (error is ArgumentException or JsonException or InputMessageSizeException or RuntimeUnitBuildException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["preview"] = ["请检查当前图、来源、草稿版本及所选记录身份。"] }); }
    }

    private static RuntimeUnitId RecentSource(RouteCompilationSnapshot snapshot, long sourceId)
    {
        if (!snapshot.Inputs.TryGetValue(sourceId, out var input) ||
            !snapshot.Devices.TryGetValue(input.DeviceId, out var device))
        { throw new ArgumentException("来源不在图内。"); }
        var connection = device.ConnectionId is { } id ? snapshot.Connections.GetValueOrDefault(id) : null;
        return RouteSourceCompiler.Compile(input, device, connection, false).Unit.Id;
    }
}
