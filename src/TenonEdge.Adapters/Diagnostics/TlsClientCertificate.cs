using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TenonEdge.Adapters.Diagnostics;

/// <summary>公开认证身份；不包含私钥、容器密码或可再次导入的凭证。</summary>
public sealed record ClientCertificateInfo(string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    string Sha256Fingerprint)
{
    public override string ToString() => $"Client certificate ({Sha256Fingerprint}; names omitted)";
}

/// <summary>
/// 有界、仅内存的客户端证书及私钥。持有者必须先关闭使用它的全部 HTTP/TLS 客户端，再释放本对象。
/// 证书链只用于向对方出示客户端身份，绝不加入服务器信任。所有建链均离线；不读文件或写系统证书库。
/// </summary>
public sealed class TlsClientCertificate : IDisposable
{
    public const int MaximumPkcs12Bytes = 64 * 1024;
    public const int MaximumPemCharacters = 96 * 1024;
    public const int MaximumPrivateKeyCharacters = 16 * 1024;
    public const int MaximumPasswordCharacters = 1024;
    public const int MaximumCertificates = 8;
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private readonly X509Certificate2Collection _certificates;
    private readonly X509Certificate2 _leaf;
    private SslStreamCertificateContext? _context;
    private bool _disposed;

    private TlsClientCertificate(X509Certificate2Collection certificates, DateTimeOffset now)
    {
        _certificates = certificates;
        if (certificates.Count is < 1 or > MaximumCertificates || certificates.Count(cert => cert.HasPrivateKey) != 1)
        { throw Invalid(); }
        _leaf = certificates.Single(cert => cert.HasPrivateKey);
        if (certificates.Select(cert => cert.GetCertHashString(HashAlgorithmName.SHA256)).Distinct(StringComparer.Ordinal).Count() != certificates.Count ||
            certificates.Any(cert => cert.RawData.Length > ServerCertificateTrust.MaximumCertificateBytes ||
                cert != _leaf && !cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)))
        { throw Invalid(); }
        if (string.IsNullOrWhiteSpace(_leaf.Subject) || _leaf.Subject.Length > 1024 || _leaf.Subject.Any(char.IsControl) ||
            _leaf.Issuer.Length > 1024 || _leaf.Issuer.Any(char.IsControl) ||
            _leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority) ||
            _leaf.Extensions.OfType<X509KeyUsageExtension>().Any(extension => (extension.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0) ||
            _leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension =>
                !extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is ClientAuthentication or "2.5.29.37.0")))
        { throw Invalid(); }
        VerifyPrivateKey(_leaf);
        ValidateForUse(now);
        Info = new(_leaf.Subject, _leaf.Issuer, _leaf.NotBefore.ToUniversalTime(), _leaf.NotAfter.ToUniversalTime(),
            _leaf.GetCertHashString(HashAlgorithmName.SHA256));
    }

    public ClientCertificateInfo Info { get; }

    /// <summary>PKCS#12 必须恰好有一份私钥；限制证书数、KDF 与 MAC，且不保留包内存储提供者/密钥路径。</summary>
    public static TlsClientCertificate FromPkcs12(ReadOnlySpan<byte> data, ReadOnlySpan<char> password, DateTimeOffset now)
    {
        if (data.IsEmpty || data.Length > MaximumPkcs12Bytes || password.Length > MaximumPasswordCharacters || password.Contains('\0'))
        { throw Invalid(); }
        X509Certificate2Collection? certificates = null;
        try
        {
            RequireSingleDerValue(data);
            certificates = X509CertificateLoader.LoadPkcs12Collection(data, password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable, new Pkcs12LoaderLimits
                {
                    MaxKeys = 1,
                    MaxCertificates = MaximumCertificates,
                    MacIterationLimit = 100_000,
                    IndividualKdfIterationLimit = 100_000,
                    TotalKdfIterationLimit = 300_000,
                });
            var result = new TlsClientCertificate(certificates, now);
            certificates = null; // 成功后所有权只交给 result。
            return result;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or AsnContentException)
        { throw Invalid(); } // 不把底层错误、容器口令或私钥带入异常链。
        finally { DisposeCertificates(certificates); }
    }

    /// <summary>PEM 证书以叶证书开头，可跟随 CA；私钥单独提供且只能有一份（PKCS#8、RSA 或 EC）。</summary>
    public static TlsClientCertificate FromPem(string certificatePem, string privateKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(certificatePem) || certificatePem.Length > MaximumPemCharacters ||
            string.IsNullOrWhiteSpace(privateKeyPem) || privateKeyPem.Length > MaximumPrivateKeyCharacters)
        { throw Invalid(); }
        var certificates = new X509Certificate2Collection();
        try
        {
            var key = privateKeyPem.AsSpan().Trim();
            if (!PemEncoding.TryFind(key, out var keyFields) || keyFields.Location.GetOffsetAndLength(key.Length) != (0, key.Length) ||
                key[keyFields.Label] is not ("PRIVATE KEY" or "RSA PRIVATE KEY" or "EC PRIVATE KEY"))
            { throw Invalid(); }
            var keyDer = Convert.FromBase64String(key[keyFields.Base64Data].ToString());
            try
            { RequireSingleDerValue(keyDer); }
            finally { CryptographicOperations.ZeroMemory(keyDer); }
            var remaining = certificatePem.AsSpan().Trim();
            while (!remaining.IsEmpty)
            {
                if (certificates.Count == MaximumCertificates || !PemEncoding.TryFind(remaining, out var fields) ||
                    fields.Location.Start.GetOffset(remaining.Length) != 0 || remaining[fields.Label] is not "CERTIFICATE" ||
                    fields.DecodedDataLength > ServerCertificateTrust.MaximumCertificateBytes)
                { throw Invalid(); }
                var block = remaining[fields.Location];
                var der = Convert.FromBase64String(remaining[fields.Base64Data].ToString());
                RequireSingleDerValue(der);
                certificates.Add(certificates.Count == 0
                    ? X509Certificate2.CreateFromPem(block, key)
                    : X509CertificateLoader.LoadCertificate(der));
                remaining = remaining[fields.Location.End.GetOffset(remaining.Length)..].Trim();
            }
            var result = new TlsClientCertificate(certificates, now);
            certificates = null!;
            return result;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or AsnContentException or FormatException)
        { throw Invalid(); }
        finally { DisposeCertificates(certificates); }
    }

    /// <summary>供 Data Protection 封装；返回内容含明文私钥，调用者须立即保护并清零，不可写日志/快照/队列。</summary>
    public byte[] ExportPkcs12()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _certificates.Export(X509ContentType.Pkcs12, string.Empty) ?? throw Invalid();
    }

    /// <summary>重连前重新检查有效期。证书选择不访问系统私钥，服务器验证与客户端身份保持分开。</summary>
    internal SslStreamCertificateContext Context(DateTimeOffset now)
    {
        ValidateForUse(now);
        return _context ??= SslStreamCertificateContext.Create(_leaf, _certificates, offline: true);
    }

    public void ValidateForUse(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (now.UtcDateTime < _leaf.NotBefore.ToUniversalTime() || now.UtcDateTime > _leaf.NotAfter.ToUniversalTime())
        { throw Invalid(); }
    }

    private static void RequireSingleDerValue(ReadOnlySpan<byte> data)
    {
        var tag = AsnDecoder.ReadEncodedValue(data, AsnEncodingRules.DER, out _, out _, out var consumed);
        if (tag != Asn1Tag.Sequence || consumed != data.Length)
        { throw Invalid(); }
    }

    private static void VerifyPrivateKey(X509Certificate2 certificate)
    {
        ReadOnlySpan<byte> challenge = "TenonEdge client certificate key check"u8;
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            using var publicKey = certificate.GetRSAPublicKey();
            if (rsa.KeySize is < 2048 or > 8192 || publicKey is null ||
                !publicKey.VerifyData(challenge, rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            { throw Invalid(); }
            return;
        }
        using var ec = certificate.GetECDsaPrivateKey();
        using var ecPublic = certificate.GetECDsaPublicKey();
        if (ec is null || ecPublic is null || ec.KeySize is not (256 or 384 or 521) ||
            !ecPublic.VerifyData(challenge, ec.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
        { throw Invalid(); }
    }

    private static ArgumentException Invalid() => new("客户端证书无效：请检查格式、大小、唯一匹配私钥、有效期和客户端认证用途。");
    private static void DisposeCertificates(X509Certificate2Collection? certificates)
    {
        if (certificates is null)
        { return; }
        foreach (var certificate in certificates)
        { certificate.Dispose(); }
    }

    public override string ToString() => "TLS client credential (private material omitted)";
    public void Dispose()
    {
        if (_disposed)
        { return; }
        _disposed = true;
        if (_context is not null)
        {
            // Create 产生的中间证书副本不是原上传集合；BCL 上下文本身没有公开 Dispose。
            foreach (var certificate in _context.IntermediateCertificates)
            { certificate.Dispose(); }
            _context = null;
        }
        DisposeCertificates(_certificates);
    }
}
