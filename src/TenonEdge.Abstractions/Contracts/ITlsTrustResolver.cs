namespace TenonEdge.Abstractions.Contracts;

/// <summary>连接级服务器信任，不是客户端认证。实现按连接及规范化接收方绑定，返回后不保留数据库事务。
/// 未设置返回 null（使用系统信任）；已设置但损坏/无法读取必须失败，不能退回系统信任。</summary>
public interface ITlsTrustResolver
{
    TlsTrustSnapshot? ResolveTlsTrust(long connectionId, string recipient);
}

public enum TlsTrustMode { PinnedCertificate, CertificateAuthority }

/// <summary>不可变的单证书快照，只含 DER 公钥证书。版本变化使原会话在下一次操作前重新握手，不改变投递接收方指纹。</summary>
public sealed record TlsTrustSnapshot(TlsTrustMode Mode, string CertificateDerBase64, string Version)
{
    public override string ToString() => $"TLS trust ({Mode}, version {Version}; certificate omitted)";
}
