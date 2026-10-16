namespace TenonEdge.Abstractions.Contracts;

/// <summary>Host 提供的当前凭证；不返回数据库客户端、请求作用域或事务。接收方变化默认不能复用旧凭证。</summary>
public interface ICredentialResolver
{
    /// <summary>非秘密身份仍可用于暂停期间的报文指纹；不提供密码，也不授权向任何地址发送。</summary>
    MqttCredentialIdentity? DescribeMqtt(long connectionId);

    MqttCredential? ResolveMqtt(long connectionId, string recipient);
}

/// <summary>只在准备与发送时取得的值对象；不写入运行快照或发送记录。</summary>
public enum MqttCredentialKind { Password, ClientCertificate }

// Password 保留旧契约名称；证书类型存的是内部规范 PKCS#12 Base64，绝不能作为 CONNECT 密码发送。
public sealed record MqttCredential(string Username, string Password, string AccountMarker, string Version,
    MqttCredentialKind Kind = MqttCredentialKind.Password, string? CertificateSubject = null)
{
    public MqttCredentialIdentity Identity => new(Username, AccountMarker, Version, Kind, CertificateSubject);
    public override string ToString() => "MQTT credential (private material omitted)";
}

public sealed record MqttCredentialIdentity(string Username, string AccountMarker, string Version,
    MqttCredentialKind Kind = MqttCredentialKind.Password, string? CertificateSubject = null);
