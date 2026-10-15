using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal sealed record ConfigurationImportCurrent(ConfigurationImportReview? Review);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationImportConfirmRequest(Guid Id, bool Confirmed);

internal static class ConfigurationImportEndpoints
{
    public static RouteGroupBuilder MapConfigurationImportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration/import").WithTags("Configuration")
            .RequireAuthorization(AuthorizationPolicies.Configurator);
        var prepare = group.MapPost("/prepare", Prepare).WithName("prepareConfigurationImport")
            .WithSummary("上传原始JSON文件并预检合并/替换；只返回本会话的完整差异，不写草稿")
            .WithDescription("最多16MiB，读取前取得共享预览预算。默认merge；replace删除文件外对象。全网关一份候选，15分钟过期，同会话新上传作废旧候选。expectedVersion必填；同connectionKey不可变更协议类型。")
            .Accepts<JsonElement>("application/json").WithMetadata(new RequestBodyLimit(ConfigurationExport.MaximumBytes + 1L))
            .Produces<ConfigurationImportReview>().ProducesValidationProblem().ProducesProblem(408).ProducesProblem(413).ProducesProblem(415);
        var current = group.MapGet("", Current).WithName("getCurrentConfigurationImport")
            .WithSummary("读取本管理会话的待确认导入；不会返回其他会话的候选")
            .Produces<ConfigurationImportCurrent>();
        var cancel = group.MapDelete("/{id:guid}", Cancel).WithName("cancelConfigurationImport")
            .WithSummary("取消本会话的导入候选，不改草稿").Produces(204);
        var confirm = group.MapPost("/confirm", Confirm).WithName("confirmConfigurationImport")
            .WithSummary("明确确认后一次性原子写入草稿及审计；不应用、不改变凭证或队列")
            .WithDescription("确认一次消费，事务内重查身份、草稿和凭证版本。失败/取消/丢回复不恢复资格，不自动重试；请读取草稿核对。")
            .WithMetadata(new RequestBodyLimit(1024)).Produces<DraftVersion>().ProducesValidationProblem().ProducesProblem(413);
        foreach (var endpoint in new[] { prepare, current, cancel, confirm })
        { endpoint.ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429); }
        return api;
    }

    private static Task Prepare(string expectedVersion, string? mode, ConfigurationImportPreparation service,
        PreviewOperationBudget budget, HttpContext context) => Execute(context, budget, async () =>
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Charset.HasValue && !contentType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        { return ApiProblems.Problem(415, "import.contentType", "请上传UTF-8的application/json原文件，不使用表单封装。"); }
        var selected = mode switch
        {
            null or "merge" => ConfigurationImportMode.Merge,
            "replace" => ConfigurationImportMode.Replace,
            _ => throw new ArgumentException("导入模式无效。"),
        };
        return TypedResults.Ok(await service.PrepareAsync(context.Request.Body, selected, expectedVersion,
            ManagementIdentity.OwnerKey(context.User)!, context.RequestAborted));
    });

    private static Task Current(ConfigurationImportPreparation service, PreviewOperationBudget budget, HttpContext context) =>
        Execute(context, budget, () => Task.FromResult<IResult>(TypedResults.Ok(new ConfigurationImportCurrent(service.Current(ManagementIdentity.OwnerKey(context.User)!)))));

    private static Task Cancel(Guid id, ConfigurationImportPreparation service, PreviewOperationBudget budget, HttpContext context) =>
        Execute(context, budget, () =>
        {
            service.Cancel(id, ManagementIdentity.OwnerKey(context.User)!);
            return Task.FromResult<IResult>(TypedResults.NoContent());
        });

    private static Task Confirm(ConfigurationImportConfirmRequest request, ConfigurationImportPreparation service,
        PreviewOperationBudget budget, HttpContext context) => Execute(context, budget, () =>
            Task.FromResult<IResult>(TypedResults.Ok(service.Confirm(request.Id, request.Confirmed,
                ManagementIdentity.OwnerKey(context.User)!, ManagementActor.Capture(context), context.RequestAborted))));

    private static async Task Execute(HttpContext context, PreviewOperationBudget budget, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var lease = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        IResult result;
        if (failure is not null)
        { result = failure; }
        else
        {
            try
            { result = await action(); }
            catch (ConfigurationImportException error)
            {
                if (error.Issues.Any(value => value.Code is "import.fileTooLarge" or "import.draftTooLarge"))
                { result = Problem(413, error.Issues[0].Code); }
                else
                {
                    var fields = error.Issues.GroupBy(value => value.Path).ToDictionary(group => group.Key, group => group.Select(value => value.Code).ToArray());
                    if (error.Truncated)
                    { fields["import"] = ["import.issuesTruncated"]; }
                    result = ApiProblems.Validation(fields);
                }
            }
            catch (ConfigurationImportRequestException error) { result = Problem(error.Status, error.Code); }
            catch (DraftWriteException error) { result = Problem(409, error.Code); }
            catch (ApiTokenException error) { result = Problem(error.Status, error.Code); }
            catch (RuntimePlanDocumentException) { result = Problem(413, "import.draftTooLarge"); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { result = Problem(408, "configuration.importUploadTimedOut"); }
            catch (ArgumentException)
            { result = ApiProblems.Validation(new Dictionary<string, string[]> { ["import"] = ["须提供有效草稿版本、merge/replace模式及本会话编号，并明确确认。"] }); }
        }
        // The diff can contain the complete before/after draft; hold the shared
        // lease through response serialization/writing, not just stream parsing.
        await result.ExecuteAsync(context);
    }

    private static ProblemHttpResult Problem(int status, string code) => ApiProblems.Problem(status, code,
        "导入未完成，请重新读取草稿并核对。确认一旦接管便不能重放；导入只改草稿，不应用配置。");
}
