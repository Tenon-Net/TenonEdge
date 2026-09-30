using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 测试账号夹具：直接在测试宿主的配置库中创建账号（总设计要求角色测试数据由测试夹具初始化，不增加生产后门）。
/// 密码经与宿主相同的 PasswordHasher 处理，ID 由宿主的雪花 ID 生成器发出。
/// </summary>
internal static class TestAccounts
{
    public static UserAccountRecord Create(
        IServiceProvider services,
        string accountName,
        string password,
        UserRole role,
        bool mustChangePassword = false,
        bool enabled = true)
    {
        var database = services.GetRequiredService<ConfigDatabase>();
        var user = new UserAccountRecord
        {
            Id = services.GetRequiredService<SnowflakeIdGenerator>().NextId(),
            AccountName = accountName,
            NormalizedAccountName = AccountNames.Normalize(accountName),
            PasswordHash = services.GetRequiredService<PasswordHashing>().Hash(password),
            MustChangePassword = mustChangePassword,
            Role = role.ToString(),
            SecurityStamp = SecurityStamps.New(),
            IsEnabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        database.Client.Insertable(user).ExecuteCommand();
        database.Client.Ado.ExecuteCommand("UPDATE system_state SET setup_state = 'initialized' WHERE id = 1 AND setup_state = 'uninitialized'");
        return user;
    }

    /// <summary>
    /// 修改账号并使会话缓存失效，与用户管理（T3-09）修改角色、停用账号时的处理相同。
    /// </summary>
    public static void Update(IServiceProvider services, string accountName, Action<UserAccountRecord> change)
    {
        var user = Find(services, accountName) ?? throw new InvalidOperationException($"测试账号 {accountName} 不存在。");
        change(user);
        services.GetRequiredService<ConfigDatabase>().Client.Updateable(user).ExecuteCommand();
        services.GetRequiredService<SessionSecurityState>().InvalidateUser(user.Id);
    }

    public static UserAccountRecord? Find(IServiceProvider services, string accountName)
    {
        var normalized = AccountNames.Normalize(accountName);
        return services.GetRequiredService<ConfigDatabase>().Client.Queryable<UserAccountRecord>()
            .First(record => record.NormalizedAccountName == normalized);
    }

    public static List<AuditLogRecord> Audit(IServiceProvider services, string action) =>
        services.GetRequiredService<ConfigDatabase>().Client.Queryable<AuditLogRecord>()
            .Where(record => record.Action == action)
            .OrderBy(record => record.Id)
            .ToList();
}
