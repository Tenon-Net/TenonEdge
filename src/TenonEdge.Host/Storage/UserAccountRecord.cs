using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 本地账号（总设计 §3.3、§10.2 User）。账号名创建后不可修改；登录与唯一性按规范化（小写）后的账号名比较。
/// 没有密码哈希表示该账号当前不能登录，例如主机侧重置之后、重新设置密码之前。时间均为 UTC Unix 毫秒。
/// </summary>
[SugarTable(TableName)]
internal sealed class UserAccountRecord
{
    public const string TableName = "user_account";

    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "account_name")]
    public string AccountName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "normalized_account_name")]
    public string NormalizedAccountName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "display_name", IsNullable = true)]
    public string? DisplayName { get; set; }

    [SugarColumn(ColumnName = "password_hash", IsNullable = true)]
    public string? PasswordHash { get; set; }

    [SugarColumn(ColumnName = "must_change_password")]
    public bool MustChangePassword { get; set; }

    /// <summary>角色名称，以字符串保存（总设计 §3.3）。</summary>
    [SugarColumn(ColumnName = "role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>安全戳：改密码、改角色、停用或主机重置时更换，使已签发的会话失效。</summary>
    [SugarColumn(ColumnName = "security_stamp")]
    public string SecurityStamp { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "failed_login_count")]
    public int FailedLoginCount { get; set; }

    [SugarColumn(ColumnName = "lockout_end", IsNullable = true)]
    public long? LockoutEnd { get; set; }

    [SugarColumn(ColumnName = "last_login_at", IsNullable = true)]
    public long? LastLoginAt { get; set; }

    [SugarColumn(ColumnName = "last_login_ip", IsNullable = true)]
    public string? LastLoginIp { get; set; }

    [SugarColumn(ColumnName = "is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }
}
