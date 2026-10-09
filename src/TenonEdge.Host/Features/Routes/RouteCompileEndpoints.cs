using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteCompileRequest(string ExpectedVersion);
internal sealed record RouteCompileResult(string Version, bool Compiled, int Sources, int Paths, int Units, string? ContentHash, RouteTopologyIssue[] Issues);

internal static class RouteCompileEndpoints
{
    public static RouteGroupBuilder MapRouteCompileEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/routes/compile", Compile).WithTags("Routes").WithName("compileRouteDrafts")
            .WithSummary("静态编译全部启用路线，不联网、不冻结版本、不启动或入队")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(4096))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<RouteCompileResult>, ValidationProblem, ProblemHttpResult> Compile(RouteCompileRequest request,
        RouteDraftStore drafts, PreviewOperationBudget budget, HttpContext context)
    {
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var snapshot = drafts.ReadCompilation(request.ExpectedVersion, context.RequestAborted);
            var result = RoutePlanCompiler.Compile(snapshot, context.RequestAborted);
            return TypedResults.Ok(new RouteCompileResult(snapshot.Version, true, result.Topology.SourcePathCounts.Count,
                result.Topology.Paths.Length, result.Document.Units.Count, result.Document.ContentHash, []));
        }
        catch (RouteTopologyException error) { return TypedResults.Ok(new RouteCompileResult(request.ExpectedVersion, false, 0, 0, 0, null, [error.Issue])); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "草稿已变化，请重新读取；未应用配置。"); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["compile"] = ["请提供有效草稿版本。"] }); }
    }
}
