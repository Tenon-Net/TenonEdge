using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Host.Features.Parameters;

internal static class ParameterSchemaEndpoints
{
    public static RouteGroupBuilder MapParameterSchemaEndpoints(this RouteGroupBuilder api)
    {
        api.MapGroup("/parameter-schemas").WithTags("参数声明")
            .MapGet("/{id}", Get).WithName("getParameterSchema")
            .Produces<ParameterSchema>().ProducesProblem(404);
        return api;
    }

    private static Results<Ok<ParameterSchema>, ProblemHttpResult> Get(string id, ParameterSchemaCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return catalog.Get(id) is { } schema ? TypedResults.Ok(schema) :
            TypedResults.Problem(statusCode: 404, title: "未安装对应参数声明。", extensions: new Dictionary<string, object?> { ["code"] = "parameters.missing" });
    }
}
