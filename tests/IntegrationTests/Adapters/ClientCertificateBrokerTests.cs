using System.Buffers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class ClientCertificateBrokerTests
{
    [Fact]
    public async Task RealMosquittoRequiresCertificateAndExchangesQosOneWithIndependentPeer()
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? "");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        using var rootKey = ECDsa.Create();
        using var root = ConnectionTrustTests.Certificate(rootKey, "client-ca", authority: true);
        using var clientKey = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(clientKey, "gateway-client", issuer: root, clientOnly: true);
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem() + "\n" + root.ExportCertificatePem(),
            clientKey.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        await using var broker = await MosquittoBroker.StartAsync(token, clientAuthorityPem: root.ExportCertificatePem());
        using var serverRoot = broker.Authority.RootCertificate;
        var trust = new TlsTrustSnapshot(TlsTrustMode.CertificateAuthority, Convert.ToBase64String(serverRoot.RawData), "fixture");
        var connector = new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy());
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "independent-" + Guid.NewGuid().ToString("N")), token);
        using var peerOwner = peer;
        var peerMessages = ReceivedMessages.Attach(peer);
        var topic = MqttProbe.NewTopic("client-certificate");
        await MqttProbe.SubscribeAsync(peer, topic + "/out", token);
        using var client = Create(material);
        var connected = await client.ConnectAsync(Options(), token);
        Assert.Equal(MqttClientConnectResultCode.Success, connected.ResultCode);
        var messages = ReceivedMessages.Attach(client);
        await MqttProbe.SubscribeAsync(client, topic + "/in", token);
        var payload = "{\"counter\":9007199254740993}"u8.ToArray();
        using var operation = MqttProbe.Timeout(token);
        var published = await MqttProbe.PublishAsync(client, topic + "/out", payload, MqttQualityOfServiceLevel.AtLeastOnce, operation.Token);
        Assert.True(published.IsSuccess);
        Assert.Equal(payload, (await peerMessages.NextAsync(token)).ApplicationMessage.Payload.ToArray());
        Assert.True((await MqttProbe.PublishAsync(peer, topic + "/in", payload, MqttQualityOfServiceLevel.AtLeastOnce, operation.Token)).IsSuccess);
        Assert.Equal(payload, (await messages.NextAsync(token)).ApplicationMessage.Payload.ToArray());
        await MqttProbe.DisconnectAsync(client, token);

        using var noCertificate = Create(null);
        using var missingDeadline = MqttProbe.Timeout(token);
        Assert.NotNull(await Record.ExceptionAsync(() => noCertificate.ConnectAsync(Options(), missingDeadline.Token)));
        Assert.False(missingDeadline.IsCancellationRequested, "应立即拒绝缺失的客户端证书。");
        Assert.False(noCertificate.IsConnected);

        using var wrongKey = ECDsa.Create();
        using var wrongCertificate = ConnectionTrustTests.Certificate(wrongKey, "untrusted-client", clientOnly: true);
        using var wrong = TlsClientCertificate.FromPem(wrongCertificate.ExportCertificatePem(), wrongKey.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        using var wrongClient = Create(wrong);
        using var wrongDeadline = MqttProbe.Timeout(token);
        Assert.NotNull(await Record.ExceptionAsync(() => wrongClient.ConnectAsync(Options(), wrongDeadline.Token)));
        Assert.False(wrongDeadline.IsCancellationRequested, "应立即拒绝不受信任的客户端证书。");
        Assert.False(wrongClient.IsConnected);
        await MqttProbe.DisconnectAsync(peer, token);

        IMqttClient Create(TlsClientCertificate? value) => new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, clientCertificate: value));
        MqttClientOptions Options() => new MqttClientOptionsBuilder().WithTcpServer("localhost", broker.TlsPort)
            .WithStreamProvider(new MqttStreamProvider(connector)).WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId("mtls-" + Guid.NewGuid().ToString("N")).WithCleanSession()
            .WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(args =>
                ServerCertificateTrust.Evaluate(args.Certificate, args.Chain, args.SslPolicyErrors, trust) is null)).Build();
    }
}
