using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Inputs;

internal static class PointCsvEndpoints
{
    public static RouteGroupBuilder MapPointCsvEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/devices/{deviceId:long}/points/csv").WithTags("Inputs")
            .RequireAuthorization(AuthorizationPolicies.Configurator);
        group.MapGet("", Export).WithName("exportDevicePointCsv").WithSummary("导出所选设备的草稿点表或模板，UTF-8 BOM，文件标注地址写法");
        group.MapPost("", Import).WithName("importDevicePointCsv")
            .WithSummary("按 key 覆盖或增量导入设备点表；有错误整批不写，不应用配置")
            .WithMetadata(new RequestBodyLimit(8 * 1024 * 1024)); // 5 MiB binary CSV encoded as JSON base64.
        return api;
    }

    private static Results<FileContentHttpResult, ValidationProblem, ProblemHttpResult> Export(long deviceId, string expectedVersion,
        bool? template, InputDraftStore store)
    {
        try
        { return TypedResults.File(store.ExportPoints(deviceId, expectedVersion, template == true), "text/csv; charset=utf-8", template == true ? "point-template.csv" : "points.csv"); }
        catch (PointCsvException exception) { return Invalid(exception); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<PointCsvImportResult>, ValidationProblem, ProblemHttpResult> Import(long deviceId,
        PointCsvImportRequest request, InputDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.ImportPoints(deviceId, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (PointCsvException exception) { return Invalid(exception); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static ValidationProblem Invalid(PointCsvException exception) => ApiProblems.Validation(exception.Issues
        .GroupBy(issue => $"csv[{issue.Line}]").ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray()));
    private static ProblemHttpResult Failure(DraftWriteException exception) => ApiProblems.Problem(
        exception.Code == "device.notFound" ? 404 : 409, exception.Code, "草稿未修改，请刷新并核对设备与版本。");
}
