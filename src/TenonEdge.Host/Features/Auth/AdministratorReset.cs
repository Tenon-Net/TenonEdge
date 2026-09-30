using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 主机侧管理员重置（功能需求 §1.5，总设计 §10.3；D-02 未确认前按“重新开放窗口加最小保护”实现，不生成设置码）：
/// 为指定的管理员清除密码、更换安全戳（已签发的会话随之失效）并解除锁定，把首次设置状态改为“重置待设置（附账号）”，并写入审计。
/// 服务下次启动时重新开放 30 分钟设置窗口，设置页只允许为该账号设置新密码，其他管理员不受影响。
/// 只能由持有数据目录锁的维护命令调用，服务运行时无法执行。
/// </summary>
internal sealed class AdministratorReset(ConfigDatabase database, AuditTrail audit)
{
    private const string MarkResetPendingSql =
        "UPDATE system_state SET setup_state = 'resetPending', setup_reset_account_id = @accountId WHERE id = 1";

    public UserAccountRecord Reset(string accountName) => database.InTransaction(client =>
    {
        var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
            ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");
        if (state.SetupState == SetupStates.Uninitialized)
        {
            throw Failed("尚未完成首次设置，没有可以重置的管理员。启动服务后直接在网页上完成首次设置即可。");
        }

        var normalized = AccountNames.Normalize(accountName);
        var user = client.Queryable<UserAccountRecord>().First(record => record.NormalizedAccountName == normalized)
            ?? throw Failed($"账号 {accountName} 不存在。");
        if (user.Role != nameof(UserRole.Administrator))
        {
            throw Failed($"账号 {user.AccountName} 不是管理员；主机侧重置只用于管理员账号。");
        }

        if (!user.IsEnabled)
        {
            throw Failed($"账号 {user.AccountName} 已停用，不能通过主机侧重置恢复。");
        }

        user.PasswordHash = null;
        user.SecurityStamp = SecurityStamps.New();
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        user.MustChangePassword = false;
        client.Updateable(user)
            .UpdateColumns(record => new { record.PasswordHash, record.SecurityStamp, record.FailedLoginCount, record.LockoutEnd, record.MustChangePassword })
            .ExecuteCommand();
        client.Ado.ExecuteCommand(MarkResetPendingSql, new { accountId = user.Id });
        audit.Append(client, new AuditEntry(
            AuditActions.AdministratorReset,
            AuditResults.Success,
            AccountName: null,
            SourceIp: null,
            Target: user.AccountName,
            Detail: $"主机侧命令，由操作系统用户 {Environment.UserName} 执行"));
        return user;
    });

    private static HostStartupException Failed(string message) => new(HostExitCodes.MaintenanceCommandFailed, message);
}
