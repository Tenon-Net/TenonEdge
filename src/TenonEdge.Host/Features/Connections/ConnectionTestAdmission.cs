using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

internal enum ConnectionTestAuthentication { None, Stored, Provided }

internal static class ConnectionTestAdmission
{
    internal static IDisposable? TryAcquire(ClaimsPrincipal user, PreviewOperationBudget budget, out ProblemHttpResult? failure)
    {
        var session = ManagementIdentity.BudgetKey(user);
        if (string.IsNullOrEmpty(session))
        {
            failure = ApiProblems.Problem(403, "preview.sessionRequired", "真实测试需要已认证的本地会话或HTTPS API令牌。");
            return null;
        }
        var lease = budget.TryAcquire(session);
        failure = lease is null ? ApiProblems.Problem(429, "preview.busy", "当前预览任务过多，请等待已有任务结束。") : null;
        return lease;
    }
}
