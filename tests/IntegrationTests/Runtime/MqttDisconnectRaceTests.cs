using System.Net;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class MqttDisconnectRaceTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    [Fact]
    public async Task SendDuringBrokerDisconnectCleanupDoesNotCountPayloadAsPoison()
    {
        var broker = fixture.Require();
        using var connector = new ClosingConnector();
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1", Port = broker.Port, UsePassword = true };
        var id = "disconnect-race-" + Guid.NewGuid().ToString("N");
        await using var session = new MqttSession(settings, id, new Credentials(), TimeProvider.System, connector);
        using var deadline = MqttProbe.Timeout(Token);
        var message = new OutputMessage("{}"u8.ToArray(), MqttProbe.NewTopic("disconnect-race"), 1,
            settings.Fingerprint(MosquittoBroker.UserName, "account"), "test");
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(message, deadline.Token)).Status);
        // 同一 ClientId 的第二条连接令真实 Broker 关闭原连接；只拉宽流释放窗口，不替换 MQTTnet 状态机。
        var (replacement, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, id), deadline.Token);
        using (replacement)
        {
            try
            {
                await connector.Closing.Task.WaitAsync(deadline.Token);
                Assert.False(session.IsConnected);
                var result = await session.SendAsync(message, deadline.Token);
                Assert.False(result.CountsTowardPoison, result.Error);
                Assert.Equal(OutputSendStatus.Ack, result.Status);
                Assert.Equal(2, connector.Connections);
            }
            finally { connector.Release.TrySetResult(); }
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Credentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(MosquittoBroker.UserName, "account", "v1");
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(MosquittoBroker.UserName, MosquittoBroker.Password, "account", "v1");
    }
    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
    private sealed class ClosingConnector : IOutboundConnector, IDisposable
    {
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stream = await new OutboundConnector(new Loopback()).ConnectAsync(host, port, cancellationToken);
            return Interlocked.Increment(ref _connections) == 1 ? new ClosingStream(stream, this) : stream;
        }
        public void Dispose()
        {
            Release.TrySetResult();
        }
    }
    private sealed class ClosingStream(Stream inner, ClosingConnector owner) : Stream
    {
        private int _disposes;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Interlocked.Increment(ref _disposes) == 1)
                {
                    owner.Closing.TrySetResult();
                    if (!owner.Release.Task.Wait(TimeSpan.FromSeconds(10)))
                    { throw new TimeoutException("测试未释放 MQTT 关闭窗口。"); }
                }
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
