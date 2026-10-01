using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 MQTT 最小原型（V-04 的 TLS 部分，验证代码，不是正式适配器）：Broker 的服务器证书由测试 CA 签发，客户端显式配置证书校验。
/// <list type="bullet">
/// <item>不信任的证书被拒绝：默认校验、另一个 CA 的信任根、主机名不符都失败；只设置 AllowUntrustedCertificates 不起作用（默认校验路径不读它）。</item>
/// <item>显式信任路径可测：提供签发 CA 作为信任根（TrustChain），或在校验回调中按管理员核对过的 SHA-256 指纹信任（总设计 §10.3、功能需求 §2.6）。</item>
/// </list>
/// 证书校验在 TLS 握手中失败，早于 MQTT 认证：ConnectAsync 抛出 MqttCommunicationException，内部为 AuthenticationException。
/// </summary>
[Trait("Category", "RealBroker")]
public sealed class MqttTlsProbeTests(MosquittoBrokerFixture fixture, ITestOutputHelper output) : IClassFixture<MosquittoBrokerFixture>
{
    [Fact]
    public async Task DefaultValidationRejectsACertificateFromAnUntrustedAuthority()
    {
        var broker = fixture.Require();

        await AssertRejectedAsync(broker, tls => tls.UseTls());
    }

    [Fact]
    public async Task AllowUntrustedCertificatesAloneDoesNotDisableValidation()
    {
        var broker = fixture.Require();

        await AssertRejectedAsync(broker, tls => tls.UseTls().WithAllowUntrustedCertificates());
    }

    [Fact]
    public async Task AnExplicitTrustChainAcceptsTheBrokerCertificate()
    {
        var broker = fixture.Require();
        using var root = broker.Authority.RootCertificate;

        await AssertAcceptedAsync(broker, tls => tls.UseTls().WithTrustChain([root]));
    }

    [Fact]
    public async Task ATrustChainOfAnotherAuthorityIsRejected()
    {
        var broker = fixture.Require();
        using var other = TestCertificateAuthority.Create("Another CA");
        using var otherRoot = other.RootCertificate;

        await AssertRejectedAsync(broker, tls => tls.UseTls().WithTrustChain([otherRoot]));
    }

    [Fact]
    public async Task AHostNameMismatchIsRejectedEvenWithTheRightTrustChain()
    {
        var broker = fixture.Require();
        using var root = broker.Authority.RootCertificate;

        await AssertRejectedAsync(broker, tls => tls.UseTls().WithTrustChain([root]).WithTargetHost("wrong.example"));
    }

    [Fact]
    public async Task APinnedFingerprintAcceptsOnlyTheExpectedCertificate()
    {
        var broker = fixture.Require();
        var expected = broker.ServerCertificateSha256;
        var other = Convert.ToHexString(SHA256.HashData("another certificate"u8));

        await AssertAcceptedAsync(broker, tls => tls.UseTls().WithCertificateValidationHandler(args => Matches(args.Certificate, expected)));
        await AssertRejectedAsync(broker, tls => tls.UseTls().WithCertificateValidationHandler(args => Matches(args.Certificate, other)));
    }

    private static bool Matches(X509Certificate certificate, string sha256) =>
        string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task AssertAcceptedAsync(MosquittoBroker broker, Action<MqttClientTlsOptionsBuilder> tls)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, _) = await MqttProbe.ConnectAsync(TlsOptions(broker, tls), cancellationToken);
        using (client)
        {
            using var publishTimeout = MqttProbe.Timeout(cancellationToken, TimeSpan.FromSeconds(5));
            var result = await MqttProbe.PublishAsync(client, MqttProbe.NewTopic("tls"), "{}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, publishTimeout.Token);
            Assert.True(result.IsSuccess);
            await MqttProbe.DisconnectAsync(client, cancellationToken);
        }
    }

    private async Task AssertRejectedAsync(MosquittoBroker broker, Action<MqttClientTlsOptionsBuilder> tls)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new MqttClientFactory().CreateMqttClient();
        using var timeout = MqttProbe.Timeout(cancellationToken);

        var exception = await Record.ExceptionAsync(() => client.ConnectAsync(TlsOptions(broker, tls), timeout.Token));
        output.WriteLine(MqttProbe.Describe(exception));

        var failure = Assert.IsType<MqttCommunicationException>(exception);
        Assert.IsType<System.Security.Authentication.AuthenticationException>(failure.InnerException);
        Assert.False(client.IsConnected);
    }

    private static MqttClientOptions TlsOptions(MosquittoBroker broker, Action<MqttClientTlsOptionsBuilder> tls) =>
        MqttProbe.Options(broker, $"probe-{Guid.NewGuid():N}", port: broker.TlsPort, tls: tls);
}
