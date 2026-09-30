using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>
/// 会话失效的判断依据（总设计 §3.3）：各账号的安全戳、角色与启停状态，以及全局的会话代次，缓存在内存中，校验会话时不写库。
/// <list type="bullet">
/// <item>修改账号安全相关字段（密码、角色、启停、重置）的代码，必须在提交后调用 <see cref="InvalidateUser"/>；
/// 缓存随后从配置库重新读取。读取与失效并发时，读取开始之后被标记失效的结果不写入缓存，改为重新读取：
/// 否则提交之前读到的旧安全戳可能在失效之后才写入缓存，旧会话继续有效、新会话反被拒绝。</item>
/// <item>会话代次在开启“只允许 HTTPS”（T5-01）或恢复备份（T5-02）时经 <see cref="IncrementGeneration"/> 加一，此前签发的全部会话失效。</item>
/// </list>
/// 每次请求由 <see cref="ValidateAsync"/> 核对票据：签发协议与当前请求协议一致、账号存在且启用、安全戳与角色未变、会话代次相同；
/// 任一不符即拒绝并删除该 Cookie，接口随之返回 401。
/// </summary>
internal sealed class SessionSecurityState
{
    private readonly ConfigDatabase _database;
    private readonly Func<long, UserSecurity> _loadUser;
    private readonly ConcurrentDictionary<long, UserSecurity> _users = new();
    private readonly Lock _usersLock = new();
    private readonly Dictionary<long, long> _invalidations = [];
    private readonly Lock _generationLock = new();
    private long? _generation;

    public SessionSecurityState(ConfigDatabase database)
        : this(database, userId => LoadUser(database, userId))
    {
    }

    /// <summary>测试用：指定读取账号状态的方法，以便在读取过程中插入失效操作。</summary>
    internal SessionSecurityState(ConfigDatabase database, Func<long, UserSecurity> loadUser)
    {
        _database = database;
        _loadUser = loadUser;
    }

    public long Generation
    {
        get
        {
            lock (_generationLock)
            {
                return _generation ??= _database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId).SessionGeneration;
            }
        }
    }

    public UserSecurity GetUser(long userId)
    {
        while (true)
        {
            if (_users.TryGetValue(userId, out var cached))
            {
                return cached;
            }

            long version;
            lock (_usersLock)
            {
                version = _invalidations.GetValueOrDefault(userId);
            }

            var loaded = _loadUser(userId);
            lock (_usersLock)
            {
                if (_invalidations.GetValueOrDefault(userId) == version)
                {
                    return _users.GetOrAdd(userId, loaded);
                }
            }
        }
    }

    public void InvalidateUser(long userId)
    {
        lock (_usersLock)
        {
            _invalidations[userId] = _invalidations.GetValueOrDefault(userId) + 1;
            _users.TryRemove(userId, out _);
        }
    }

    public long IncrementGeneration()
    {
        lock (_generationLock)
        {
            _database.InTransaction(client => client.Ado.ExecuteCommand(
                "UPDATE system_state SET session_generation = session_generation + 1 WHERE id = 1"));
            _generation = null;
            return Generation;
        }
    }

    private static UserSecurity LoadUser(ConfigDatabase database, long userId)
    {
        var user = database.Client.Queryable<UserAccountRecord>().InSingle(userId);
        return user is null ? UserSecurity.Missing : new UserSecurity(true, user.IsEnabled, user.SecurityStamp, user.Role);
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var state = context.HttpContext.RequestServices.GetRequiredService<SessionSecurityState>();
        var valid = principal is not null
            && principal.FindFirst(SessionClaims.Protocol)?.Value == RequestProtocol.Name(context.HttpContext)
            && long.TryParse(principal.FindFirst(SessionClaims.UserId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            && long.TryParse(principal.FindFirst(SessionClaims.SessionGeneration)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            && generation == state.Generation
            && state.GetUser(userId) is { Exists: true, Enabled: true } user
            && user.SecurityStamp == principal.FindFirst(SessionClaims.SecurityStamp)?.Value
            && user.Role == principal.FindFirst(SessionClaims.Role)?.Value;
        if (!valid)
        {
            context.RejectPrincipal();
            await SessionAuthentication.SignOutAsync(context.HttpContext);
        }
    }
}

/// <summary>账号与会话失效相关的状态。</summary>
internal sealed record UserSecurity(bool Exists, bool Enabled, string SecurityStamp, string Role)
{
    public static UserSecurity Missing { get; } = new(false, false, string.Empty, string.Empty);
}
