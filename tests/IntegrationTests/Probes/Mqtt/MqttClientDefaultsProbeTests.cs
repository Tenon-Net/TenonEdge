using MQTTnet;
using MQTTnet.Formatter;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11：MQTTnet 5.2.0 客户端选项的默认值（S-02，验证代码），说明适配器为什么必须显式设置协议版本、会话、ClientId、超时与 TLS 校验。
/// 不连接 Broker，CI 中照常运行。
/// </summary>
[Trait("Category", "MqttProbe")]
public sealed class MqttClientDefaultsProbeTests
{
    [Fact]
    public void DefaultsDoNotMatchTheDesignAndMustBeSetExplicitly()
    {
        var first = new MqttClientOptionsBuilder().WithTcpServer("broker.example").Build();
        var second = new MqttClientOptionsBuilder().WithTcpServer("broker.example").Build();

        // 默认 MQTT 5.0：仅设置 CleanSession=false 不能保留会话（会话过期间隔默认 0），设计要求显式 3.1.1。
        Assert.Equal(MqttProtocolVersion.V500, first.ProtocolVersion);
        Assert.Equal(0u, first.SessionExpiryInterval);
        Assert.True(first.CleanSession);

        // ClientId 默认随机：持久会话需要稳定的 ClientId。
        Assert.False(string.IsNullOrEmpty(first.ClientId));
        Assert.NotEqual(first.ClientId, second.ClientId);

        // 只有不可取消的令牌才使用这里的超时；PublishAsync 完全不用它。保活默认 15 秒。
        Assert.Equal(TimeSpan.FromSeconds(100), first.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), first.KeepAlivePeriod);
    }

    [Fact]
    public void TlsDefaultsCheckRevocationOnlineAndDoNotTrustCustomRoots()
    {
        var tls = new MqttClientTlsOptions();

        // 离线现场或私有 CA 时在线吊销检查可能失败；信任管理员的证书须显式提供信任根或校验回调。
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online, tls.RevocationMode);
        Assert.Null(tls.TrustChain);
        Assert.Null(tls.CertificateValidationHandler);
        Assert.False(tls.AllowUntrustedCertificates);
    }
}
