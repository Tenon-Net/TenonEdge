using System.Security.Cryptography;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>当前凭证决定认证方式；旧快照 UsePassword 仅保留“凭证缺失时不得匿名”的约束。</summary>
public static class MqttCredentialValidation
{
    public static void Validate(MqttCredential credential, bool tls)
    {
        if (credential.Kind == MqttCredentialKind.ClientCertificate)
        {
            if (!tls || credential.Username != string.Empty || string.IsNullOrWhiteSpace(credential.CertificateSubject) ||
                credential.CertificateSubject.Length > 1024 || string.IsNullOrEmpty(credential.Password) ||
                credential.Password.Length > 4 * ((TlsClientCertificate.MaximumPkcs12Bytes + 2) / 3))
            { throw Invalid(); }
            return;
        }
        if (credential.Kind != MqttCredentialKind.Password || credential.CertificateSubject is not null ||
            string.IsNullOrEmpty(credential.Username) || credential.Username.Length > 256 || credential.Username.Contains('\0', StringComparison.Ordinal) ||
            credential.Password is null || credential.Password.Length > 4096 || credential.Password.Contains('\0', StringComparison.Ordinal))
        { throw Invalid(); }
    }

    internal static MqttCredential? Resolve(MqttConnectionSettings settings, ICredentialResolver resolver)
    {
        var credential = resolver.ResolveMqtt(settings.ConnectionId, settings.Recipient);
        if (credential is null && (settings.UsePassword || resolver.DescribeMqtt(settings.ConnectionId) is not null))
        { throw Invalid(); }
        if (credential is not null)
        { Validate(credential, settings.Tls); }
        return credential;
    }

    internal static TlsClientCertificate? OpenCertificate(MqttCredential? credential, DateTimeOffset now)
    {
        if (credential?.Kind != MqttCredentialKind.ClientCertificate)
        { return null; }
        Validate(credential, tls: true);
        byte[] data;
        try
        { data = Convert.FromBase64String(credential.Password); }
        catch (FormatException) { throw Invalid(); }
        try
        {
            var material = TlsClientCertificate.FromPkcs12(data, [], now);
            if (material.Info.Subject == credential.CertificateSubject)
            { return material; }
            material.Dispose();
            throw Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static ArgumentException Invalid() => new("MQTT 凭证未设置、已作废或无效，请核对认证类型、材料及 TLS 设置。");
}
