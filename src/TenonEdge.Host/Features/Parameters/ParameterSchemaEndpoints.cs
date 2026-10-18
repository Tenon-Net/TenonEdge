using System.Collections.Immutable;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Outputs;
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
        api.MapGroup("/input-modules").WithTags("输入模块")
            .MapGet("/", GetInputs).WithName("getInputModules")
            .Produces<ImmutableArray<InputModuleDefinition>>();
        api.MapGroup("/output-modules").WithTags("输出模块")
            .MapGet("/", GetOutputs).WithName("getOutputModules")
            .Produces<ImmutableArray<OutputModuleDefinition>>();
        api.MapGroup("/connection-modules").WithTags("连接模块")
            .MapGet("/", GetConnections).WithName("getConnectionModules")
            .Produces<ImmutableArray<ConnectionModuleDefinition>>();
        return api;
    }

    private static Ok<ImmutableArray<ConnectionModuleDefinition>> GetConnections(ConnectionModuleCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(catalog.Modules);
    }

    private static Ok<ImmutableArray<OutputModuleDefinition>> GetOutputs(OutputModuleCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(catalog.Modules);
    }

    private static Ok<ImmutableArray<InputModuleDefinition>> GetInputs(InputModuleCatalog catalog, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(catalog.Modules);
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
