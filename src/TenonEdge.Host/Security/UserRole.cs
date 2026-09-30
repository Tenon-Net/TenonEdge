namespace TenonEdge.Host.Security;

/// <summary>
/// 固定的三种角色（总设计 §3.3、§10.3）：管理员、配置人员、只读。以名称字符串保存，API 中按字符串序列化；
/// 各角色的权限在 <see cref="AuthorizationPolicies"/> 中集中定义。
/// </summary>
internal enum UserRole
{
    Administrator,
    Configurator,
    Viewer,
}
