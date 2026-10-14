using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 首次设置（总设计 §10.3，功能需求 §1.3；D-01 未确认前按“首次访问设置加最小保护”实现，不要求设置码）：
/// <list type="bullet">
/// <item>只在状态为“未初始化”或“重置待设置”、且处于设置窗口内时可用。</item>
/// <item>提交时在同一个写事务中以比较并交换的方式把状态改为“已初始化”：未初始化时创建管理员；重置待设置时只为该账号设置新密码，
/// 其他管理员不受影响。两人同时提交时只有一个成功，另一个得到“已有管理员”。</item>
/// <item>记录完成时间与来源 IP（首页与系统信息显示），并写入审计。</item>
/// </list>
/// 校验与密码哈希在事务之外完成，写事务只做状态检查与写入。
/// </summary>
internal sealed class SetupService(
    ConfigDatabase database,
    SetupWindow window,
    PasswordHashing hashing,
    SnowflakeIdGenerator ids,
    AuditTrail audit,
    SessionSecurityState sessions,
    TimeProvider time)
{
    public const int GatewayNameMaximumLength = GatewayNameRules.MaximumLength;

    private const string CompleteSql = """
        UPDATE system_state
        SET setup_state = 'initialized', setup_reset_account_id = NULL, setup_completed_at = @completedAt,
            setup_source_ip = @sourceIp, gateway_name = @gatewayName
        WHERE id = 1 AND setup_state = @expectedState
        """;

    public SetupStatus GetStatus()
    {
        var state = ReadState(database.Client);
        var open = state.SetupState != SetupStates.Initialized && window.IsOpen;
        var resetAccount = open && state.SetupState == SetupStates.ResetPending && state.SetupResetAccountId is { } accountId
            ? database.Client.Queryable<UserAccountRecord>().InSingle(accountId)?.AccountName
            : null;
        return new SetupStatus(state.SetupState, open, open ? window.ClosesAt : null, resetAccount);
    }

    public SetupOutcome Complete(string? accountName, string? password, string? gatewayName, string? sourceIp)
    {
        var state = ReadState(database.Client);
        if (state.SetupState == SetupStates.Initialized)
        {
            return new SetupOutcome.NotAvailable();
        }

        if (!window.IsOpen)
        {
            return new SetupOutcome.WindowClosed();
        }

        var resetAccount = state.SetupState == SetupStates.ResetPending && state.SetupResetAccountId is { } resetId
            ? database.Client.Queryable<UserAccountRecord>().InSingle(resetId)
            : null;
        var errors = Validate(accountName, password, gatewayName, resetAccount, out var normalizedGatewayName);
        if (errors.Count > 0)
        {
            return new SetupOutcome.Invalid(errors);
        }

        var passwordHash = hashing.Hash(password!);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using var client = database.CreateIsolatedClient();
        client.Ado.BeginTran();
        try
        {
            var current = ReadState(client);
            if (current.SetupState == SetupStates.Initialized
                || current.SetupState != state.SetupState
                || current.SetupResetAccountId != state.SetupResetAccountId)
            {
                client.Ado.RollbackTran();
                return new SetupOutcome.NotAvailable();
            }

            if (!window.IsOpen)
            {
                client.Ado.RollbackTran();
                return new SetupOutcome.WindowClosed();
            }

            var user = resetAccount is null
                ? CreateAdministrator(client, accountName!, passwordHash, now)
                : SetNewPassword(client, resetAccount.Id, passwordHash);

            var updated = client.Ado.ExecuteCommand(CompleteSql, new
            {
                completedAt = now,
                sourceIp,
                gatewayName = normalizedGatewayName ?? current.GatewayName,
                expectedState = current.SetupState,
            });
            if (updated != 1)
            {
                client.Ado.RollbackTran();
                return new SetupOutcome.NotAvailable();
            }

            audit.Append(client, new AuditEntry(
                AuditActions.SetupCompleted,
                AuditResults.Success,
                user.AccountName,
                sourceIp,
                Target: user.AccountName,
                Detail: current.SetupState == SetupStates.ResetPending ? "主机侧重置后设置新密码" : "首次设置，创建管理员"));
            client.Ado.CommitTran();
            sessions.InvalidateUser(user.Id);
            return new SetupOutcome.Completed(user);
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    private static SystemStateRecord ReadState(ISqlSugarClient client) =>
        client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
        ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");

    private static Dictionary<string, string[]> Validate(
        string? accountName,
        string? password,
        string? gatewayName,
        UserAccountRecord? resetAccount,
        out string? normalizedGatewayName)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string effectiveAccountName;
        if (resetAccount is not null)
        {
            effectiveAccountName = resetAccount.AccountName;
            if (accountName is not null && AccountNames.Normalize(accountName) != resetAccount.NormalizedAccountName)
            {
                errors["accountName"] = [$"只能为账号 {resetAccount.AccountName} 设置新密码。"];
            }
        }
        else
        {
            effectiveAccountName = accountName ?? string.Empty;
            if (!AccountNames.IsValid(accountName))
            {
                errors["accountName"] = [$"账号只能包含字母、数字和 -、_、.，长度 {AccountNames.MinimumLength}—{AccountNames.MaximumLength} 位。"];
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            errors["password"] = ["请输入密码。"];
        }
        else if (PasswordPolicy.Validate(password, effectiveAccountName) is { } reason)
        {
            errors["password"] = [reason];
        }

        try
        { normalizedGatewayName = GatewayNameRules.Normalize(gatewayName); }
        catch (ArgumentException exception)
        {
            normalizedGatewayName = null;
            errors["gatewayName"] = [exception.Message];
        }

        return errors;
    }

    private UserAccountRecord CreateAdministrator(SqlSugarClient client, string accountName, string passwordHash, long now)
    {
        var user = new UserAccountRecord
        {
            Id = ids.NextId(),
            AccountName = accountName,
            NormalizedAccountName = AccountNames.Normalize(accountName),
            PasswordHash = passwordHash,
            Role = nameof(UserRole.Administrator),
            SecurityStamp = SecurityStamps.New(),
            IsEnabled = true,
            CreatedAt = now,
        };
        client.Insertable(user).ExecuteCommand();
        return user;
    }

    private static UserAccountRecord SetNewPassword(SqlSugarClient client, long accountId, string passwordHash)
    {
        var user = client.Queryable<UserAccountRecord>().InSingle(accountId)
            ?? throw new InvalidOperationException("等待设置新密码的账号不存在。");
        user.PasswordHash = passwordHash;
        user.SecurityStamp = SecurityStamps.New();
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        client.Updateable(user)
            .UpdateColumns(record => new { record.PasswordHash, record.SecurityStamp, record.MustChangePassword, record.FailedLoginCount, record.LockoutEnd })
            .ExecuteCommand();
        return user;
    }
}

/// <summary>首次设置的状态。</summary>
internal sealed record SetupStatus(string State, bool WindowOpen, DateTimeOffset? WindowClosesAt, string? ResetAccountName);

/// <summary>提交首次设置的结果。</summary>
internal abstract record SetupOutcome
{
    public sealed record Completed(UserAccountRecord User) : SetupOutcome;

    /// <summary>已经完成设置（包括并发提交中晚到的一方）。</summary>
    public sealed record NotAvailable : SetupOutcome;

    public sealed record WindowClosed : SetupOutcome;

    public sealed record Invalid(IDictionary<string, string[]> Errors) : SetupOutcome;
}
