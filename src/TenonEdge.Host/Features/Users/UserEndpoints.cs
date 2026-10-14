using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Users;

internal static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/users").WithTags("Users").RequireAuthorization(AuthorizationPolicies.Administrator)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("", List).WithName("listUsers").WithSummary("管理员分页查看本地账号，不返回密码哈希或安全戳")
            .WithDescription("按ID升序，每页最多100条。Version绑定管理字段和锁定状态；不是登录凭据。登录时间变化不使管理版本失效。");
        group.MapPost("", Create).WithName("createUser").WithSummary("创建使用临时密码的本地账号")
            .WithDescription("账号名不可修改、忽略大小写唯一；显示名最多128字符。必须改临时密码后才能进入系统。操作与审计同事务，不自动重放。");
        group.MapPut("/{userId:long}", Update).WithName("updateUser").WithSummary("修改显示名、固定角色及启停状态；安全变更结束全部会话");
        group.MapPost("/{userId:long}/password", ResetPassword).WithName("resetUserPassword").WithSummary("设置临时密码并结束全部会话");
        group.MapPost("/{userId:long}/unlock", (long userId, UserCommandRequest request, UserManagementService users, HttpContext context) =>
            Command(userId, UserOperation.Unlock, request, users, context)).WithName("unlockUser").WithSummary("提前解除登录锁定，不自动登录或启用账号");
        group.MapPost("/{userId:long}/end-sessions", (long userId, UserCommandRequest request, UserManagementService users, HttpContext context) =>
            Command(userId, UserOperation.EndSessions, request, users, context)).WithName("endUserSessions").WithSummary("结束该用户全部已签发会话，不停用账号");
        group.MapDelete("/{userId:long}", (long userId, string? expectedVersion, bool confirmed, string? expectedTokenVersion, bool? revokeTokens, UserManagementService users, HttpContext context) =>
            Command(userId, UserOperation.Delete, new(expectedVersion, confirmed, expectedTokenVersion, revokeTokens), users, context)).WithName("deleteUser")
            .WithSummary("删除本地账号，保留原账号的审计；不能删除自己或最后一个启用管理员");
        return api;
    }

    private static Results<Ok<UserPage>, ProblemHttpResult> List(long? after, int? limit, UserManagementService users, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(users.Read(after ?? 0, limit ?? 50)); }
        catch (UserManagementException error) { return Failure(error); }
        catch (ApiTokenException error) { return ApiTokenEndpoints.Problem(error); }
        catch (SqliteException) { return Unavailable(); }
    }
    private static Task<Results<Ok<UserMutationResult>, ProblemHttpResult>> Create(UserCreateRequest request, UserManagementService users, HttpContext context) =>
        Write(() => users.CreateAsync(request, ManagementActor.Capture(context), context.RequestAborted), context);
    private static Task<Results<Ok<UserMutationResult>, ProblemHttpResult>> Update(long userId, UserUpdateRequest request, UserManagementService users, HttpContext context) =>
        Write(() => users.ChangeAsync(userId, UserOperation.Update, request.ExpectedVersion, request.Confirmed, ManagementActor.Capture(context), request, null, context.RequestAborted), context);
    private static Task<Results<Ok<UserMutationResult>, ProblemHttpResult>> ResetPassword(long userId, UserPasswordResetRequest request, UserManagementService users, HttpContext context) =>
        Write(() => users.ChangeAsync(userId, UserOperation.ResetPassword, request.ExpectedVersion, request.Confirmed, ManagementActor.Capture(context), null, request.TemporaryPassword, context.RequestAborted), context);
    private static Task<Results<Ok<UserMutationResult>, ProblemHttpResult>> Command(long id, UserOperation operation, UserCommandRequest request, UserManagementService users, HttpContext context) =>
        Write(() => users.ChangeAsync(id, operation, request.ExpectedVersion, request.Confirmed, ManagementActor.Capture(context), null, null, context.RequestAborted, request.ExpectedTokenVersion, request.RevokeTokens), context);
    private static async Task<Results<Ok<UserMutationResult>, ProblemHttpResult>> Write(Func<Task<UserMutationResult>> work, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(await work()); }
        catch (UserManagementException error) { return Failure(error); }
        catch (ApiTokenException error) { return ApiTokenEndpoints.Problem(error); }
        catch (SqliteException) { return Unavailable(); }
    }
    private static ProblemHttpResult Failure(UserManagementException error) => ApiProblems.Problem(error.Status, error.Code,
        "用户操作未执行，请核对权限、最新用户信息、确认和参数。最后一个启用管理员不能被删除、停用或降级，且不能删除自己。");
    private static ProblemHttpResult Unavailable() => ApiProblems.Problem(503, "user.storageUnavailable", "用户存储暂不可用，未报告成功；请重新读取后操作，不自动重放。");
}
