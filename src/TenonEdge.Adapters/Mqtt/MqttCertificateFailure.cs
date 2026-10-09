using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>只解释平台 TLS 校验结果，不建立新的信任链、不改变接受条件、不保留证书正文。</summary>
internal static class MqttCertificateFailure
{
    internal static string? Classify(SslPolicyErrors errors, X509Chain? chain, DateTime utcNow)
    {
        if (errors == SslPolicyErrors.None)
        { return null; }
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        { return "certificate.missing"; }
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        { return "certificate.nameMismatch"; }
        var flags = chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, item) => all | item.Status)
            ?? X509ChainStatusFlags.NoError;
        if ((flags & X509ChainStatusFlags.Revoked) != 0)
        { return "certificate.revoked"; }
        if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            // 中间证书/根证书也可能过期，不能仅根据叶证书给出错误结论。
            foreach (var element in chain!.ChainElements)
            {
                if (element.ChainElementStatus.Any(status => (status.Status & X509ChainStatusFlags.NotTimeValid) != 0))
                {
                    if (element.Certificate.NotAfter.ToUniversalTime() < utcNow)
                    { return "certificate.expired"; }
                    if (element.Certificate.NotBefore.ToUniversalTime() > utcNow)
                    { return "certificate.notYetValid"; }
                }
            }
            return "certificate.timeInvalid";
        }
        if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
        { return "certificate.untrusted"; }
        if ((flags & (X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.RevocationStatusUnknown)) != 0)
        { return "certificate.revocationUnavailable"; }
        return "certificate.invalid";
    }
}
