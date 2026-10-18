using System.Collections.Immutable;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Host.Features.Parameters;

internal static class ParameterSchemaEndpoints
{
    public static RouteGroupBuilder MapParameterSchemaEndpoints(this RouteGroupBuilder api)
    {
        api.MapGroup("/parameter-schemas").WithTags("参数声明")
            .MapGet("/{id}", Get).WithName("getParameterSchema")
            .Produces<ParameterSchema>().ProducesProblem(404);
        api.MapGroup("/transform-modules").WithTags("转换模块")
            .MapGet("/", GetTransforms).WithName("getTransformModules")
            .Produces<ImmutableArray<TransformModuleDefinition>>();
        return api;
    }

    private static Ok<ImmutableArray<TransformModuleDefinition>> GetTransforms(TransformCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(catalog.Modules);
    }

    private static Results<Ok<ParameterSchema>, ProblemHttpResult> Get(string id, ParameterSchemaCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return catalog.Get(id) is { } schema ? TypedResults.Ok(schema) :
            TypedResults.Problem(statusCode: 404, title: "未安装对应参数声明。", extensions: new Dictionary<string, object?> { ["code"] = "parameters.missing" });
    }
}
