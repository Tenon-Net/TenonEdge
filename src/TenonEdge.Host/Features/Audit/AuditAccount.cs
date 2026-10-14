namespace TenonEdge.Host.Features.Audit;

/// <summary>已经认证的非秘密审计身份快照。由请求入口捕获并显式传给后台操作，不能在后台读取请求上下文。
/// 不代表授权；令牌名称不代替令牌编号/吊销检查。禁止隐式转回字符串，避免后台链路静默丢失令牌身份。</summary>
internal readonly record struct AuditAccount(string? Name, string? TokenName = null)
{
    public static implicit operator AuditAccount(string? name) => new(name);
}
