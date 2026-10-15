using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ConnectionTrustRotationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionSessionReplacesOldTransportBeforeSendingAfterTrustChanges(bool http)
    {
        using var key = ECDsa.Create();
        using var first = ConnectionTrustTests.Certificate(key, "localhost");
        using var second = ConnectionTrustTests.Certificate(key, "localhost");
        await using var peer = new Peer(http, first);
        var recipient = http ? $"https://localhost:{peer.Port}/" : $"mqtts://localhost:{peer.Port}";
        var trust = new ConnectionTrustTests.TrustResolver(1, recipient, Snapshot(first, "v1"));
        var connector = new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy());
        await using var httpSession = new HttpConnectionSession(connector, 5000, trust);
        var mqttSettings = new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", Port = peer.Port, Tls = true };
        await using var mqttSession = new MqttSession(mqttSettings, "rotation-test", new NoCredentials(), TimeProvider.System, connector, trust: trust);
        var httpSettings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = recipient, Path = "data", Inputs = ["device/input"] };
        var message = http ? new OutputMessage("{}"u8.ToArray(), "data", null, httpSettings.Fingerprint(null), "fixture",
            ImmutableDictionary<string, string>.Empty.Add("Idempotency-Key", Guid.NewGuid().ToString("D")), HttpFrozenRequest.Freeze(httpSettings))
            : new OutputMessage("{}"u8.ToArray(), "test/topic", 1, mqttSettings.Fingerprint(null, null), "fixture");
        Assert.Equal(OutputSendStatus.Ack, (await Send()).Status);
        Assert.Equal(1, peer.Connections);
        Assert.Equal(1, peer.Messages);
        Assert.Equal(OutputSendStatus.Ack, (await Send()).Status);
        Assert.Equal(1, peer.Connections); // Unchanged trust reuses original session.
        Assert.Equal(2, peer.Messages);

        // Server changes its next-handshake certificate, but the old connection is still usable.
        // A trust revision must force a new handshake instead of sending on that old stream.
        peer.Certificate = second;
        trust.Value = Snapshot(first, "v2");
        Assert.Equal(OutputSendStatus.SuspendTarget, (await Send()).Status);
        Assert.Equal(2, peer.Connections);
        Assert.Equal(2, peer.Messages);
        trust.Value = Snapshot(second, "v3");
        Assert.Equal(OutputSendStatus.Ack, (await Send()).Status);
        Assert.Equal(3, peer.Connections);
        Assert.Equal(3, peer.Messages);
        trust.Value = new(TlsTrustMode.PinnedCertificate, "corrupt", "v4");
        Assert.Equal(OutputSendStatus.SuspendTarget, (await Send()).Status);
        Assert.Equal(3, peer.Messages); // A damaged stored certificate never falls back to a previously verified stream.
        trust.Value = Snapshot(second, "v5");
        Assert.Equal(OutputSendStatus.Ack, (await Send()).Status);
        Assert.Equal(4, peer.Messages);
        trust.Value = null;
        Assert.Equal(OutputSendStatus.SuspendTarget, (await Send()).Status);
        Assert.Equal(4, peer.Messages); // Clearing goes back to system verification, not trust-all.

        Task<OutputSendResult> Send() => http ? httpSession.SendOutputAsync(httpSettings, new NoCredentials(), TimeProvider.System, message, Token)
            : mqttSession.SendAsync(message, Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitTestSendUsesTheSameTrustAndNeverBypassesInvalidCertificate(bool http)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "localhost");
        await using var peer = new Peer(http, certificate);
        var recipient = http ? $"https://localhost:{peer.Port}/" : $"mqtts://localhost:{peer.Port}";
        var trust = new ConnectionTrustTests.TrustResolver(1, recipient, Snapshot(certificate, "1"));
        var connector = new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy());
        var httpSettings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = recipient, Path = "data", Inputs = ["device/input"] };
        var mqttSettings = new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", Port = peer.Port, Tls = true };
        var httpSender = new HttpTestMessageSender(new SharedConnections(), connector, TimeProvider.System, trust);
        var mqttSender = new MqttTestMessageSender(connector, TimeProvider.System, trust);
        var httpMessage = new OutputMessage("{}"u8.ToArray(), "data", null, httpSettings.Fingerprint(null), "fixture",
            ImmutableDictionary<string, string>.Empty.Add("Idempotency-Key", Guid.NewGuid().ToString("D")), HttpFrozenRequest.Freeze(httpSettings));
        var mqttMessage = new OutputMessage("{}"u8.ToArray(), "test/topic", 1, mqttSettings.Fingerprint(null, null), "fixture");
        Assert.True(await Send());
        Assert.Equal(1, peer.Messages);
        trust.Value = new(TlsTrustMode.PinnedCertificate, "corrupt", "2");
        Assert.False(await Send());
        Assert.Equal(1, peer.Messages);
        async Task<bool> Send() => http ? (await httpSender.SendAsync(httpSettings, null, httpMessage, Token)).StatusCode == 200
            : (await mqttSender.SendAsync(mqttSettings, null, mqttMessage, Token)).BrokerAcknowledged;
    }

    [Fact]
    public async Task HttpLiveReadAndFullSampleUseCurrentTrustWithoutChangingAccountFingerprint()
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "localhost");
        await using var peer = new Peer(true, certificate);
        var recipient = $"https://localhost:{peer.Port}/";
        var trust = new ConnectionTrustTests.TrustResolver(1, recipient, Snapshot(certificate, "1"));
        var connector = new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy());
        var connections = new SharedConnections();
        var credentials = new NoCredentials();
        var settings = new HttpInputSettings
        {
            ConnectionId = 1,
            BaseAddress = recipient,
            Path = "read",
            Fields = JsonSerializer.Deserialize<JsonElement>("""[{"key":"value","path":"/value","type":"integer"}]""")
        };
        var preview = new HttpReadPreview(connections, connector, credentials, new JsonInputDecoderFactory(), TimeProvider.System, trust);
        Assert.True((await preview.RunAsync(settings, ClockSyncState.Synchronized, Token)).Success);
        var sample = await preview.ReadSampleAsync(settings, "source", "device", ClockSyncState.Synchronized, Token);
        Assert.Equal("9223372036854775807", sample.Message!.Values["value"].ToString());
        var target = new HttpOutputTarget(RuntimeUnitId.Create("output.http", "test"), new HttpOutputSettings
        { ConnectionId = 1, BaseAddress = recipient, Path = "data", Inputs = ["device/input"] }, credentials, TimeProvider.System, connector, connections, trust);
        var fingerprint = target.CurrentFingerprint;
        var version = target.CredentialVersion;
        trust.Value = trust.Value! with { Version = "2", CertificateDerBase64 = "corrupt" };
        Assert.Equal("certificate.invalid", (await preview.RunAsync(settings, ClockSyncState.Synchronized, Token)).Code);
        Assert.Equal(2, peer.Messages);
        Assert.Equal(fingerprint, target.CurrentFingerprint);
        Assert.NotEqual(version, target.CredentialVersion);
        trust.Value = Snapshot(certificate, "3");
        Assert.True((await preview.RunAsync(settings, ClockSyncState.Synchronized, Token)).Success);
        Assert.Equal(3, peer.Messages);
    }

    private static TlsTrustSnapshot Snapshot(X509Certificate2 certificate, string version) =>
        new(TlsTrustMode.PinnedCertificate, Convert.ToBase64String(certificate.RawData), version);

    private sealed class NoCredentials : ICredentialResolver, IHttpCredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }

    internal sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        private int _connections;
        private int _messages;
        public int Connections => Volatile.Read(ref _connections);
        public int Messages => Volatile.Read(ref _messages);
        public X509Certificate2 Certificate { get; set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Peer(bool http, X509Certificate2 certificate)
        {
            Certificate = certificate;
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            _listener.Start();
            _accept = AcceptAsync(http);
        }

        private async Task AcceptAsync(bool http)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    _clients.Add(ServeAsync(client, Certificate, http));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client, X509Certificate2 certificate, bool http)
        {
            using (client)
            using (var stream = new SslStream(client.GetStream()))
            {
                var token = _stop.Token;
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
                    if (http)
                    {
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        while (await reader.ReadLineAsync(token) is { } request)
                        {
                            var get = request.StartsWith("GET ", StringComparison.Ordinal);
                            Assert.True(get || request.StartsWith("POST ", StringComparison.Ordinal));
                            var length = 0;
                            while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
                            {
                                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                { length = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture); }
                            }
                            var body = new char[length];
                            if (length > 0)
                            { Assert.Equal(length, await reader.ReadBlockAsync(body, token)); }
                            Assert.Equal(get ? "" : "{}", new string(body));
                            Interlocked.Increment(ref _messages);
                            var payload = get ? "{\"value\":9223372036854775807}" : "";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n\r\n{payload}"), token);
                        }
                    }
                    else
                    {
                        while (true)
                        {
                            var one = new byte[1];
                            if (await stream.ReadAsync(one, token) == 0)
                            { break; }
                            var header = one[0];
                            var length = 0;
                            var shift = 0;
                            do
                            {
                                await stream.ReadExactlyAsync(one, token);
                                length |= (one[0] & 0x7f) << shift;
                                shift += 7;
                                Assert.InRange(shift, 7, 28);
                            } while ((one[0] & 0x80) != 0);
                            Assert.InRange(length, 0, 4096);
                            var body = new byte[length];
                            await stream.ReadExactlyAsync(body, token);
                            if (header == 0x10)
                            { await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token); }
                            else if (header == 0x32)
                            {
                                var topicLength = (body[0] << 8) | body[1];
                                Assert.Equal("{}", Encoding.UTF8.GetString(body[(topicLength + 4)..]));
                                Interlocked.Increment(ref _messages);
                                await stream.WriteAsync(new byte[] { 0x40, 2, body[topicLength + 2], body[topicLength + 3] }, token);
                            }
                            else if (header == 0xc0)
                            { await stream.WriteAsync(new byte[] { 0xd0, 0 }, token); }
                            else
                            { Assert.Fail($"Unexpected MQTT header: {header}"); }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or AuthenticationException || exception is OperationCanceledException && token.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept;
            _listener.Stop();
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
