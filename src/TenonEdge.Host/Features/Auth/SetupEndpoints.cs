using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>首次设置接口（auth 分组，总设计 §10.1、§10.3）：两个接口都是匿名例外。</summary>
internal static class SetupEndpoints
{
    public const string AlreadyCompletedCode = "setup.alreadyCompleted";

    public const string WindowClosedCode = "setup.windowClosed";

    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/setup", GetStatus)
            .AllowAnonymous()
            .WithTags("Setup")
            .WithName("getSetupStatus")
            .WithSummary("首次设置状态")
            .WithDescription("未登录即可查询：当前状态、设置窗口是否开放及关闭时间；重置待设置时给出等待设置新密码的账号。");

        api.MapPost("/setup", CompleteAsync)
            .AllowAnonymous()
            .WithTags("Setup")
            .WithName("completeSetup")
            .WithSummary("完成首次设置")
            .WithDescription("未初始化时创建管理员；重置待设置时为被重置的账号设置新密码。成功后自动登录。已有管理员或窗口已关闭时返回 409。")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static Ok<SetupStatusResponse> GetStatus(SetupService setup)
    {
        var status = setup.GetStatus();
        return TypedResults.Ok(new SetupStatusResponse(ToState(status.State), status.WindowOpen, status.WindowClosesAt, status.ResetAccountName));
    }

    private static async Task<Results<Ok<SessionResponse>, ValidationProblem, ProblemHttpResult>> CompleteAsync(
        CompleteSetupRequest request,
        SetupService setup,
        HttpContext context)
    {
        switch (setup.Complete(request.AccountName, request.Password, request.GatewayName, SourceAddress.Of(context)))
        {
            case SetupOutcome.Completed completed:
                var csrfToken = await SessionAuthentication.SignInAsync(context, completed.User);
                return TypedResults.Ok(SessionResponse.From(completed.User, csrfToken));
            case SetupOutcome.Invalid invalid:
                return ApiProblems.Validation(invalid.Errors);
            case SetupOutcome.WindowClosed:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, WindowClosedCode, "设置窗口已关闭，请重启服务后再设置。");
            default:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, AlreadyCompletedCode, "已有管理员，请登录。");
        }
    }

    private static SetupState ToState(string state) => state switch
    {
        SetupStates.Initialized => SetupState.Initialized,
        SetupStates.ResetPending => SetupState.ResetPending,
        _ => SetupState.Uninitialized,
    };
}
