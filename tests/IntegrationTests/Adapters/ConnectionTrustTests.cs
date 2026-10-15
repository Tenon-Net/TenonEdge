using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

/// <summary>真实 TCP/SslStream 握手，测试侧只实现最小 HTTP 响应/MQTT CONNACK，不宣称 Broker 或实物兼容验收。</summary>
public sealed class ConnectionTrustTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<bool, string, string?> Cases => new()
    {
        { true, "pin", null }, { false, "pin", null },
        { true, "signedPin", null }, { false, "signedPin", null },
        { true, "ca", null }, { false, "ca", null },
        { true, "aiaCa", null }, { false, "aiaCa", null },
        { true, "intermediateCa", null }, { false, "intermediateCa", null },
        { true, "renewedCa", null }, { false, "renewedCa", null },
        { true, "changedPin", "certificate.changed" }, { false, "changedPin", "certificate.changed" },
        { true, "wrongCa", "certificate.untrusted" }, { false, "wrongCa", "certificate.untrusted" },
        { true, "namePin", "certificate.nameMismatch" }, { false, "namePin", "certificate.nameMismatch" },
        { true, "nameCa", "certificate.nameMismatch" }, { false, "nameCa", "certificate.nameMismatch" },
        { true, "expiredPin", "certificate.expired" }, { false, "expiredPin", "certificate.expired" },
        { true, "expiredCa", "certificate.expired" }, { false, "expiredCa", "certificate.expired" },
        { true, "futurePin", "certificate.notYetValid" }, { false, "futurePin", "certificate.notYetValid" },
        { true, "futureCa", "certificate.notYetValid" }, { false, "futureCa", "certificate.notYetValid" },
        { true, "usagePin", "certificate.invalid" }, { false, "usagePin", "certificate.invalid" },
        { true, "usageCa", "certificate.invalid" }, { false, "usageCa", "certificate.invalid" },
        { true, "corrupt", "certificate.trustInvalid" }, { false, "corrupt", "certificate.trustInvalid" },
        { true, "wrongConnection", "certificate.untrusted" }, { false, "wrongConnection", "certificate.untrusted" },
        { true, "wrongRecipient", "certificate.untrusted" }, { false, "wrongRecipient", "certificate.untrusted" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TrustModesValidateActualHandshakesBeforeAnyApplicationBytes(bool http, string scenario, string? expected)
    {
        using var downloads = new TcpListener(IPAddress.Loopback, 0);
        downloads.Start();
        var issuerUrl = $"http://127.0.0.1:{((IPEndPoint)downloads.LocalEndpoint).Port}/issuer.der";
        using var key = ECDsa.Create();
        using var ca = Certificate(key, "test-root", authority: true);
        using var otherKey = ECDsa.Create();
        using var otherCa = Certificate(otherKey, "other-root", authority: true);
        using var intermediateKey = ECDsa.Create();
        using var intermediate = Certificate(intermediateKey, "intermediate", authority: true, issuer: ca);
        using var serverKey = ECDsa.Create();
        var usesCa = scenario.EndsWith("Ca", StringComparison.Ordinal) || scenario is "ca" or "signedPin";
        using var server = Certificate(serverKey, scenario.StartsWith("name", StringComparison.Ordinal) ? "wrong.invalid" : "localhost",
            issuer: scenario == "intermediateCa" ? intermediate : usesCa ? ca : null, expired: scenario.StartsWith("expired", StringComparison.Ordinal),
            future: scenario.StartsWith("future", StringComparison.Ordinal), clientOnly: scenario.StartsWith("usage", StringComparison.Ordinal), issuerUrl: scenario == "aiaCa" ? issuerUrl : null);
        using var first = Certificate(serverKey, "localhost", issuer: ca);
        var mode = usesCa && scenario != "signedPin" ? TlsTrustMode.CertificateAuthority : TlsTrustMode.PinnedCertificate;
        var anchor = scenario switch
        {
            "intermediateCa" => intermediate,
            "wrongCa" => otherCa,
            "changedPin" => first,
            _ => mode == TlsTrustMode.CertificateAuthority ? ca : server,
        };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var recipient = http ? $"https://localhost:{port}/" : $"mqtts://localhost:{port}";
        var trust = new TrustResolver(scenario == "wrongConnection" ? 2 : 1,
            scenario == "wrongRecipient" ? recipient + "different" : recipient,
            new(mode, scenario == "corrupt" ? "not-a-certificate" : Convert.ToBase64String(anchor.RawData), "v1"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var token = stop.Token;
        var appBytes = 0;
        var peer = PeerAsync();
        var result = await ProbeAsync();
        await peer;
        Assert.False(downloads.Pending()); // No TLS AIA request may bypass the outbound connector.
        Assert.Equal(expected is null, result.Success);
        Assert.Equal(expected ?? (http ? "http.response" : "success"), result.Code);
        Assert.Equal(expected is null, appBytes > 0);
        Assert.NotNull(result.Certificate);
        Assert.Equal(server.GetCertHashString(HashAlgorithmName.SHA256), result.Certificate.Sha256Fingerprint);
        Assert.Equal(server.RawData, Convert.FromBase64String(result.Certificate.CertificateDerBase64));
        Assert.Equal("localhost", trust.LastHost);
        Assert.DoesNotContain("not-sent-before-tls", result.Certificate.ToString(), StringComparison.Ordinal);

        async Task<(bool Success, string Code, ServerCertificateInfo? Certificate)> ProbeAsync()
        {
            var connector = new OutboundConnector(new LoopbackPolicy());
            if (http)
            {
                var value = await new HttpConnectionTest(new SharedConnections(), connector, TimeProvider.System, trust).RunAsync(
                    new HttpRequestSettings { ConnectionId = 1, BaseAddress = recipient },
                    new HttpCredential(new(HttpCredentialKind.Bearer, null, null, "a", "1"), "not-sent-before-tls"), token);
                return (value.Success, value.Code, value.Certificate);
            }
            var mqtt = await new MqttConnectionTest(connector, TimeProvider.System, trust).RunAsync(
                new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", Port = port, Tls = true, UsePassword = true },
                new MqttCredential("fixture", "not-sent-before-tls", "a", "1"), token);
            return (mqtt.Success, mqtt.Code, mqtt.Certificate);
        }

        async Task PeerAsync()
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            using var stream = new SslStream(accepted.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = SslStreamCertificateContext.Create(server, null, offline: true) }, token);
                var buffer = new byte[4096];
                appBytes = await stream.ReadAsync(buffer, token);
                if (appBytes > 0)
                {
                    await stream.WriteAsync(http ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()
                        : new byte[] { 0x20, 2, 0, 0 }, token);
                    if (!http)
                    { _ = await stream.ReadAsync(buffer, token); }
                }
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException) { }
        }
    }

    internal static X509Certificate2 Certificate(ECDsa key, string name, bool authority = false, X509Certificate2? issuer = null,
        bool expired = false, bool future = false, bool clientOnly = false, string? issuerUrl = null)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        if (!authority)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], true));
        }
        if (issuerUrl is not null)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
                writer.WriteCharacterString(UniversalTagNumber.IA5String, issuerUrl, new Asn1Tag(TagClass.ContextSpecific, 6));
            }
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.1", writer.Encode(), false));
        }
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(future ? 1 : -3);
        var to = now.AddDays(expired ? -1 : authority ? 10 : 3);
        if (issuer is null)
        { return request.CreateSelfSigned(from, to); }
        // Root is created milliseconds earlier; keep issued certificates strictly within its validity.
        from = from > issuer.NotBefore.ToUniversalTime() ? from : issuer.NotBefore.ToUniversalTime().AddSeconds(1);
        if (to >= issuer.NotAfter.ToUniversalTime())
        { to = issuer.NotAfter.ToUniversalTime().AddSeconds(-1); }
        using var issued = request.Create(issuer, from, to, RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    internal sealed class TrustResolver(long connectionId, string recipient, TlsTrustSnapshot? initial) : ITlsTrustResolver
    {
        public TlsTrustSnapshot? Value { get; set; } = initial;
        public string? LastHost { get; private set; }
        public TlsTrustSnapshot? ResolveTlsTrust(long id, string target)
        {
            LastHost = new Uri(target).Host;
            return id == connectionId && target == recipient ? Value : null;
        }
    }

    internal sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
