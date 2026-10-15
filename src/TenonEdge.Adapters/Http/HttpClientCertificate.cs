using System.Security.Cryptography;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>当前 HTTP 凭证与 TLS 材料之间的边界；上传解析在 Host 完成，这里只接受规范空口令容器。</summary>
internal static class HttpClientCertificate
{
    internal static TlsClientCertificate? Open(HttpCredential? credential, DateTimeOffset now)
    {
        if (credential?.Identity.Kind != HttpCredentialKind.ClientCertificate)
        { return null; }
        HttpCredentialValidation.Validate(credential.Identity.Kind, credential.Identity.Username, credential.Identity.Name, credential.Secret);
        byte[] data;
        try
        { data = Convert.FromBase64String(credential.Secret); }
        catch (FormatException) { throw new ArgumentException("HTTP 客户端证书凭证无效。"); }
        try
        {
            var material = TlsClientCertificate.FromPkcs12(data, [], now);
            if (material.Info.Subject == credential.Identity.CertificateSubject)
            { return material; }
            material.Dispose();
            throw new ArgumentException("HTTP 客户端证书身份不匹配。");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
