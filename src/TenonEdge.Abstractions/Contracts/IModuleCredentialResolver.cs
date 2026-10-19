using System.Collections.Immutable;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>进程级的当前扩展连接凭证。按冻结接收方解析，不从未应用草稿改变目标。
/// 适配器每次认证/请求取当前版本；不得在快照、队列、日志中保存秘密。</summary>
public interface IModuleCredentialResolver
{
    ModuleCredentialIdentity? DescribeModule(long connectionId);
    ModuleCredential? ResolveModule(ModuleConnectionSnapshot connection);
}

public sealed record ModuleCredentialIdentity(string Kind, int ModuleVersion, string Recipient,
    string Account, string AccountMarker, string Version);

/// <summary>只在使用时取得的值对象。序列化/ToString不暴露秘密；不持有数据库或请求。</summary>
public sealed class ModuleCredential
{
    private readonly ImmutableDictionary<string, string> _secrets;
    public ModuleCredential(ModuleCredentialIdentity identity, IReadOnlyDictionary<string, string> secrets)
    {
        Identity = identity;
        _secrets = secrets.ToImmutableDictionary(StringComparer.Ordinal);
        Names = _secrets.Keys.Order(StringComparer.Ordinal).ToImmutableArray();
    }
    public ModuleCredentialIdentity Identity { get; }
    public ImmutableArray<string> Names { get; }
    public string GetSecret(string name) => _secrets[name];
    public override string ToString() => "Module credential (private material omitted)";
}
