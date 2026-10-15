using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class MqttConnectionTrustBrokerTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TlsTrustMode.PinnedCertificate)]
    [InlineData(TlsTrustMode.CertificateAuthority)]
    public async Task HostRegisteredTrustIsSharedByConnectionTestRealPublishAndDiagnosticSubscription(TlsTrustMode mode)
    {
        var broker = fixture.Require();
        using var directory = TemporaryDirectory.Create();
        var deployment = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = broker.TlsPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await using var factory = new FixedDataDirectoryApplicationFactory(directory.Path, deployment);
        var admin = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var api = await factory.CreateLoggedInClientAsync("admin", Token);
        var drafts = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = drafts.Write(null, new(drafts.Read().Version, "broker", "Broker", ConnectionKind.Mqtt,
            Mqtt: new("localhost", broker.TlsPort, Tls: true)), "fixture", null);
        var settings = new MqttConnectionSettings { ConnectionId = draft.Connection.Id, Host = "localhost", Port = broker.TlsPort, Tls = true, UsePassword = true };
        var credential = new MqttCredential(MosquittoBroker.UserName, MosquittoBroker.Password, "fixture", "1");
        // Real API authentication is TestServer; its outgoing TLS handshake is an actual socket to Mosquitto.
        var request = new
        {
            connection = new { host = settings.Host, port = settings.Port, tls = true },
            connectionId = settings.ConnectionId,
            authentication = "provided",
            username = credential.Username,
            password = credential.Password
        };
        using var first = await api.PostJsonAsync("/api/edge-admin/v1/connections/test/mqtt", request, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var untrusted = await first.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token);
        Assert.Equal("certificate.untrusted", untrusted!.Code);
        Assert.NotNull(untrusted.Certificate);
        var certificate = mode == TlsTrustMode.PinnedCertificate ? untrusted.Certificate : Root();
        var store = factory.Services.GetRequiredService<ConnectionTrustStore>();
        Assert.Same(store, factory.Services.GetRequiredService<ITlsTrustResolver>());
        store.Write(settings.ConnectionId, new(draft.Version, settings.Recipient, mode, certificate.CertificateDerBase64,
            certificate.Sha256Fingerprint, true), new(admin.Id, admin.SecurityStamp, null));
        using var trusted = await api.PostJsonAsync("/api/edge-admin/v1/connections/test/mqtt", request, Token);
        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
        Assert.True((await trusted.Content.ReadFromJsonAsync<MqttConnectionTestResult>(Token))!.Success);

        var topic = MqttProbe.NewTopic("connection-trust");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "trust-observer-" + Guid.NewGuid().ToString("N")), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var payload = "{\"value\":9223372036854775807}"u8.ToArray();
            var sent = await factory.Services.GetRequiredService<MqttTestMessageSender>().SendAsync(settings, credential,
                new(payload, topic, 1, settings.Fingerprint(credential.Username, credential.AccountMarker), "fixture"), Token);
            Assert.True(sent.BrokerAcknowledged);
            Assert.Equal(payload, (await received.NextAsync(Token)).ApplicationMessage.Payload.ToArray());
            await subscriber.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
            factory.Services.GetRequiredService<MqttCredentialStore>().Write(settings.ConnectionId,
                new(settings.Recipient, credential.Username, credential.Password, ExpectedDraftVersion: draft.Version), "fixture", null, true);
            var input = new MqttInputSettings
            {
                ConnectionId = settings.ConnectionId,
                Host = settings.Host,
                Port = settings.Port,
                Tls = true,
                UsePassword = true,
                TopicFilter = topic,
                Qos = 1,
                ProcessRetained = false,
                Fields = JsonSerializer.SerializeToElement(Array.Empty<object>())
            };
            var sample = await factory.Services.GetRequiredService<MqttReadPreview>().RunAsync(input, ClockSyncState.Synchronized, Token);
            Assert.Equal("success", sample.Code);
            Assert.True(sample.Retained);
            Assert.Equal("{\"value\":9223372036854775807}", sample.Preview!.RawJson);
        }

        ServerCertificateInfo Root()
        {
            using var root = broker.Authority.RootCertificate;
            return ServerCertificateTrust.Describe(root)!;
        }
    }
}
