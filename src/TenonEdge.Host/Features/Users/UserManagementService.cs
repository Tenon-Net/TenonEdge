using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Users;

/// <summary>复用本地账号、哈希、按账号串行和安全戳。每次写入在同一个BEGIN IMMEDIATE事务内核对操作者、
/// 目标版本和最后管理员，再写账号与审计。事务不跨密码哈希、网络或await；单例不保存请求作用域对象。</summary>
internal sealed class UserManagementService(ConfigDatabase database, PasswordHashing hashing, LoginLockout lockout,
    SessionSecurityState sessions, AccountService accounts, AuditTrail audit, ApiTokenStore tokens, SnowflakeIdGenerator ids, TimeProvider time)
{
    public UserPage Read(long after, int limit)
    {
        if (after is < 0 or > 9007199254740991 || limit is < 1 or > 100)
        { throw new UserManagementException("user.invalidQuery", 400); }
        var users = database.Client.Queryable<UserAccountRecord>().Where(user => user.Id > after).OrderBy(user => user.Id).Take(limit + 1).ToList();
        return new(users.Take(limit).Select(user => UserSummary.From(user, time.GetUtcNow())).ToArray(), users.Count > limit ? users[limit - 1].Id : null);
    }

    public async Task<UserMutationResult> CreateAsync(UserCreateRequest request, ManagementActor actor, CancellationToken token)
    {
        if (!AccountNames.IsValid(request.AccountName))
        { throw new UserManagementException("user.invalidAccountName", 400); }
        var role = Role(request.Role);
        var display = DisplayName(request.DisplayName);
        var accountName = request.AccountName!;
        var normalized = AccountNames.Normalize(accountName);
        Password(request.TemporaryPassword, accountName);
        using var gate = await lockout.EnterAsync(normalized, token);
        var hash = hashing.Hash(request.TemporaryPassword!);
        token.ThrowIfCancellationRequested();
        var user = database.InTransaction(client =>
        {
            var administrator = RequireActor(client, actor);
            if (client.Queryable<UserAccountRecord>().Any(record => record.NormalizedAccountName == normalized))
            { throw new UserManagementException("user.accountExists"); }
            var created = new UserAccountRecord
            {
                Id = ids.NextId(),
                AccountName = accountName,
                NormalizedAccountName = normalized,
                DisplayName = display,
                Role = role.ToString(),
                IsEnabled = request.IsEnabled,
                PasswordHash = hash,
                MustChangePassword = true,
                SecurityStamp = SecurityStamps.New(),
                CreatedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
            };
            client.Insertable(created).ExecuteCommand();
            WriteAudit(client, administrator, actor.SourceIp, created, "user.create");
            return created;
        });
        sessions.InvalidateUser(user.Id);
        return new(UserSummary.From(user, time.GetUtcNow()));
    }

    public async Task<UserMutationResult> ChangeAsync(long id, UserOperation operation, string? expectedVersion, bool confirmed,
        ManagementActor actor, UserUpdateRequest? update, string? password, CancellationToken token, string? expectedTokenVersion = null, bool? revokeTokens = null)
    {
        if (id is < 1 or > 9007199254740991 || expectedVersion is not { Length: 64 } || !confirmed)
        { throw new UserManagementException("user.invalidConfirmation", 400); }
        var original = database.Client.Queryable<UserAccountRecord>().InSingle(id)
            ?? throw new UserManagementException("user.notFound", 404);
        UserRole? newRole = update is null ? null : Role(update.Role);
        var display = update is null ? null : DisplayName(update.DisplayName);
        if (operation == UserOperation.ResetPassword)
        { Password(password, original.AccountName); }
        // 同一个账号的登录、自己改密码和管理员操作使用同一组有界信号量，避免旧密码校验后管理员先重置/删除。
        using var gate = await lockout.EnterAsync(original.NormalizedAccountName, token);
        var hash = operation == UserOperation.ResetPassword ? hashing.Hash(password!) : null;
        token.ThrowIfCancellationRequested();
        var invalidate = false;
        var changed = database.InTransaction<UserAccountRecord?>(client =>
        {
            var administrator = RequireActor(client, actor);
            var user = client.Queryable<UserAccountRecord>().InSingle(id) ?? throw new UserManagementException("user.notFound", 404);
            if (!string.Equals(expectedVersion, UserSummary.VersionOf(user), StringComparison.Ordinal))
            { throw new UserManagementException("user.changed"); }
            if (operation == UserOperation.Delete && actor.UserId == id)
            { throw new UserManagementException("user.cannotDeleteSelf"); }
            var losesAdministrator = operation == UserOperation.Delete || update is not null && (!update.IsEnabled || newRole != UserRole.Administrator);
            if (user.IsEnabled && user.Role == nameof(UserRole.Administrator) && losesAdministrator &&
                client.Queryable<UserAccountRecord>().Count(record => record.IsEnabled && record.Role == nameof(UserRole.Administrator)) <= 1)
            { throw new UserManagementException("user.lastAdministrator"); }

            if (operation == UserOperation.Delete || update is { IsEnabled: false } && user.IsEnabled)
            { tokens.ApplyCreatorChoice(client, id, update?.ExpectedTokenVersion ?? expectedTokenVersion, update?.RevokeTokens ?? revokeTokens, administrator, actor.SourceIp); }

            switch (operation)
            {
                case UserOperation.Update:
                    if (update is null || newRole is null)
                    { throw new InvalidOperationException("用户更新缺少参数。"); }
                    if (user.DisplayName == display && user.Role == newRole.ToString() && user.IsEnabled == update.IsEnabled)
                    { throw new UserManagementException("user.noChanges"); }
                    if (user.DisplayName != display)
                    { WriteAudit(client, administrator, actor.SourceIp, user, "user.rename"); }
                    if (user.Role != newRole.ToString())
                    { WriteAudit(client, administrator, actor.SourceIp, user, "user.changeRole"); invalidate = true; }
                    if (user.IsEnabled != update.IsEnabled)
                    { WriteAudit(client, administrator, actor.SourceIp, user, update.IsEnabled ? "user.enable" : "user.disable"); invalidate = true; }
                    user.DisplayName = display;
                    user.Role = newRole.ToString()!;
                    user.IsEnabled = update.IsEnabled;
                    break;
                case UserOperation.ResetPassword:
                    user.PasswordHash = hash;
                    user.MustChangePassword = true;
                    user.FailedLoginCount = 0;
                    user.LockoutEnd = null;
                    invalidate = true;
                    WriteAudit(client, administrator, actor.SourceIp, user, "user.resetPassword");
                    break;
                case UserOperation.Unlock:
                    if (AccountLockouts.LockedUntil(user, time.GetUtcNow()) is null)
                    { throw new UserManagementException("user.notLocked"); }
                    user.FailedLoginCount = 0;
                    user.LockoutEnd = null;
                    WriteAudit(client, administrator, actor.SourceIp, user, "user.unlock");
                    break;
                case UserOperation.EndSessions:
                    invalidate = true;
                    WriteAudit(client, administrator, actor.SourceIp, user, "user.endSessions");
                    break;
                case UserOperation.Delete:
                    client.Deleteable<UserAccountRecord>().In(id).ExecuteCommand();
                    // 审计只保留原账号文字，不建级联删除；主键高水位也不随删除倒退。
                    client.Ado.ExecuteCommand("UPDATE system_state SET id_floor = MAX(id_floor, @id) WHERE id=1", new { id });
                    WriteAudit(client, administrator, actor.SourceIp, user, "user.delete");
                    invalidate = true;
                    return null;
                default:
                    throw new InvalidOperationException("未知用户操作。");
            }
            if (invalidate)
            { user.SecurityStamp = SecurityStamps.New(); }
            client.Updateable(user).ExecuteCommand();
            return user;
        });
        if (invalidate)
        {
            sessions.InvalidateUser(id);
            accounts.ClearPasswordAttempts(id);
        }
        return new(changed is null ? null : UserSummary.From(changed, time.GetUtcNow()));
    }

    private AuditAccount RequireActor(SqlSugarClient client, ManagementActor actor)
    {
        try
        { return actor.RequireAdministrator(client, time.GetUtcNow().ToUnixTimeMilliseconds()).Audit; }
        catch (ApiTokenException) { throw new UserManagementException("user.actorChanged", 401); }
    }
    private void WriteAudit(ISqlSugarClient client, AuditAccount administrator, string? sourceIp, UserAccountRecord user, string action) =>
        audit.Append(client, new AuditEntry(action, AuditResults.Success, administrator, sourceIp, user.AccountName));
    private static UserRole Role(UserRole? role) => role is { } value && Enum.IsDefined(value) ? value : throw new UserManagementException("user.invalidRole", 400);
    private static string? DisplayName(string? display)
    {
        display = display?.Trim();
        if (display is { Length: > 128 } || display?.Any(char.IsControl) == true)
        { throw new UserManagementException("user.invalidDisplayName", 400); }
        return string.IsNullOrEmpty(display) ? null : display;
    }
    private static void Password(string? password, string accountName)
    {
        if (password is null || PasswordPolicy.Validate(password, accountName) is not null)
        { throw new UserManagementException("user.invalidPassword", 400); }
    }
}
