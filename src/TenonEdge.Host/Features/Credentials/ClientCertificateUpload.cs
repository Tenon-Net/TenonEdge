using System.Security.Cryptography;
using System.Text.Json.Serialization;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>只用于显式写入或临时测试；不回显、持久化上传请求或容器口令。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClientCertificateUpload(string? Pkcs12 = null, string? Password = null,
    string? CertificatePem = null, string? PrivateKeyPem = null)
{
    internal TlsClientCertificate Open(DateTimeOffset now)
    {
        if (Pkcs12 is not null)
        {
            if (CertificatePem is not null || PrivateKeyPem is not null || Pkcs12.Length > 4 * ((TlsClientCertificate.MaximumPkcs12Bytes + 2) / 3))
            { throw Invalid(); }
            byte[] data;
            try
            { data = Convert.FromBase64String(Pkcs12); }
            catch (FormatException) { throw Invalid(); }
            try
            { return TlsClientCertificate.FromPkcs12(data, Password ?? string.Empty, now); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        if (Password is not null || CertificatePem is null || PrivateKeyPem is null)
        { throw Invalid(); }
        return TlsClientCertificate.FromPem(CertificatePem, PrivateKeyPem, now);
    }

    private static ArgumentException Invalid() => new("请选择一份 PKCS#12 与可选口令，或 PEM 证书链与未加密私钥，不能混用。");
    public override string ToString() => "Client certificate upload (private material omitted)";
}
