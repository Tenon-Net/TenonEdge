namespace TenonEdge.Host.Security;

/// <summary>安全戳：随机值，账号的密码、角色、启停或重置发生变化时更换，使已签发的会话失效（总设计 §3.3）。</summary>
internal static class SecurityStamps
{
    public static string New() => Guid.NewGuid().ToString("N");
}
