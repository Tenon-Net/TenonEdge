using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteFieldRequest(string ExpectedVersion, RouteGraph Graph, string NodeId, string? AfterKey = null, string? Search = null, int Limit = 100);

internal static class RouteFieldEndpoints
{
    public static RouteGroupBuilder MapRouteFieldEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/routes/fields", Read).WithTags("Routes").WithName("analyzeRouteFields")
            .WithSummary("分析未保存图中所选节点的上游字段，不保存、不联网或入队，不代替全图编译")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }
    private static Results<Ok<RouteFieldPage>, ValidationProblem, ProblemHttpResult> Read(RouteFieldRequest request,
        RouteDraftStore drafts, PreviewOperationBudget budget, TenonEdge.Runtime.Transforms.TransformCatalog transforms, TenonEdge.Runtime.Inputs.InputModuleCatalog inputModules, TenonEdge.Runtime.Outputs.OutputModuleCatalog outputModules, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var prefix = RouteFieldAnalysis.Prefix(request.Graph, request.NodeId, transforms, inputModules, outputModules);
            var snapshot = drafts.ReadGraphCompilation(request.ExpectedVersion, prefix, context.RequestAborted);
            return TypedResults.Ok(RouteFieldAnalysis.Analyze(snapshot, request.NodeId, request.AfterKey, request.Search, request.Limit, context.RequestAborted));
        }
        catch (RouteTopologyException error)
        { return TypedResults.Ok(new RouteFieldPage(request.ExpectedVersion, request.NodeId, false, [], null, 0, 0, false, [], false, [error.Issue])); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "草稿已变化，请重新读取；未保存或应用配置。"); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["fields"] = ["请检查图格式、节点标识、版本和分页参数。"] }); }
    }
}
