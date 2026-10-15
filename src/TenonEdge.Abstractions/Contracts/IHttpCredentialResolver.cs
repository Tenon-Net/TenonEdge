namespace TenonEdge.Abstractions.Contracts;

/// <summary>当前 HTTP 凭证与非秘密身份。实现必须按基础接收方绑定秘密，不向运行快照或队列泄露它。</summary>
public interface IHttpCredentialResolver
{
    HttpCredentialIdentity? DescribeHttp(long connectionId);
    HttpCredential? ResolveHttp(long connectionId, string recipient);
}

public enum HttpCredentialKind { Basic, Bearer, ApiKeyHeader, ApiKeyQuery, ClientCertificate }
public sealed record HttpCredentialIdentity(HttpCredentialKind Kind, string? Username, string? Name, string AccountMarker, string Version,
    string? CertificateSubject = null);
// ClientCertificate 的 Secret 是仅供内部使用的规范 PKCS#12 Base64，仍属于秘密，不可序列化为管理响应或快照。
public sealed record HttpCredential(HttpCredentialIdentity Identity, string Secret)
{
    public override string ToString() => $"HTTP credential ({Identity.Kind}, version {Identity.Version})";
}
