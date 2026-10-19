using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>测试侧有界 TCP 帧传输。只提供原输出接口，不创建发送循环或队列。</summary>
internal sealed class TestWireOutputTransport(ISharedConnections connections, IOutboundConnector outbound) : IOutputAdapterFactory
{
    private readonly ISharedConnections _connections = connections;
    private readonly IOutboundConnector _outbound = outbound;
    private int _builds;
    private int _adapters;
    private int _sessions;
    private int _disposedSessions;
    public int Builds => Volatile.Read(ref _builds);
    public int Adapters => Volatile.Read(ref _adapters);
    public int Sessions => Volatile.Read(ref _sessions);
    public int DisposedSessions => Volatile.Read(ref _disposedSessions);
    public bool PrefixUnitKind { get; set; }
    public Func<OutputMessageContent, OutputMessageContent>? ContentTransform { get; set; }
    public string? RecipientOverride { get; set; }
    public string? MessageAddressOverride { get; set; }
    public string? MessageSummaryOverride { get; set; }
    public string? MessageFingerprintOverride { get; set; }
    public long? TargetConnectionIdOverride { get; set; }
    public string[]? TargetInputsOverride { get; set; }
    public string Kind => "output.wire";
    public int QueueBindings { get; private set; }
    public bool QueueBindingThrows { get; set; }

    public JsonElement? BindQueueOnly(ModuleConnectionSnapshot connection)
    {
        QueueBindings++;
        if (QueueBindingThrows)
        { throw new InvalidOperationException("fixture-secret"); }
        var settings = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
        return JsonSerializer.SerializeToElement(new
        {
            connectionId = connection.ConnectionId,
            inputs = Array.Empty<string>(),
            queueOnly = true,
            connection,
            transportRoute = new
            {
                recipient = $"wire://{settings.GetProperty("host").GetString()}:{settings.GetProperty("port").GetInt32()}",
                addressTemplate = "",
            },
        }, JsonSerializerOptions.Web);
    }

    public OutputMessageContent FormatMessage(RuntimeUnitPlan plan, EdgeMessage message, Guid deliveryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Format(plan.Id, OutputTransportRoute.Read(plan.Configuration, Kind, required: true)!, message, deliveryId);
    }

    private OutputMessageContent Format(RuntimeUnitId id, OutputTransportRoute route, EdgeMessage message, Guid deliveryId)
    {
        var content = new OutputMessageContent(StandardEnvelope.Serialize(message, deliveryId), MessageAddressOverride ?? route.AddressFor(message.DeviceKey));
        if (PrefixUnitKind)
        { content = content with { Payload = Encoding.UTF8.GetBytes(id.Kind + ":" + Encoding.UTF8.GetString(content.Payload.Span)) }; }
        return ContentTransform?.Invoke(content) ?? content;
    }

    public IOutputTarget Build(RuntimeUnitPlan plan)
    {
        Interlocked.Increment(ref _builds);
        var route = OutputTransportRoute.Read(plan.Configuration, Kind, required: true)!;
        var connection = plan.Configuration.GetProperty("connection");
        var settings = connection.GetProperty("configuration").GetProperty("connectionModule").GetProperty("parameters");
        return new Target(this, plan.Id, TargetConnectionIdOverride ?? plan.Configuration.GetProperty("connectionId").GetInt64(),
            TargetInputsOverride ?? plan.Configuration.GetProperty("inputs").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            settings.GetProperty("host").GetString()!, settings.GetProperty("port").GetInt32(), route);
    }

    private sealed class Target(TestWireOutputTransport owner, RuntimeUnitId id, long connectionId, string[] inputs,
        string host, int port, OutputTransportRoute route) : IOutputTarget
    {
        public RuntimeUnitId Id => id;
        public long ConnectionId => connectionId;
        public IReadOnlyList<string> Inputs => inputs;
        public string? ClientId => null;
        public string Recipient => owner.RecipientOverride ?? $"wire://{host}:{port}";
        public string CredentialVersion => "anonymous";
        public string CurrentFingerprint => CaptureIdentity().Fingerprint;
        public OutputTargetIdentity CaptureIdentity()
        {
            var identity = OutputRecipientIdentity.Extension("wire", Recipient, "none", null);
            return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Freeze()))), identity);
        }
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
        {
            var identity = CaptureIdentity();
            var content = owner.Format(Id, route, message, deliveryId);
            return new(content.Payload, content.Address, content.QualityOfService,
                owner.MessageFingerprintOverride ?? identity.Fingerprint, owner.MessageSummaryOverride ?? identity.Summary,
                content.Headers, content.ProtocolMetadata);
        }
        public bool CanShareSenderWith(IOutputTarget other) => other is Target target && ConnectionId == target.ConnectionId && Recipient == target.Recipient;
        public IOutputAdapter CreateAdapter()
        {
            Interlocked.Increment(ref owner._adapters);
            return new Adapter(owner._connections.Acquire(ConnectionId, "wire", Recipient, () =>
            {
                Interlocked.Increment(ref owner._sessions);
                return new Session(owner, host, port);
            }));
        }
    }

    private sealed class Adapter(IConnectionLease<Session> lease) : IOutputAdapter
    {
        public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            { return await lease.ExecuteAsync((session, token) => session.SendAsync(message, token), deadline.Token); }
            catch (OutboundAccessDeniedException) { return new(OutputSendStatus.SuspendTarget, "wire.outboundDenied"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new(OutputSendStatus.Retry, "wire.timeout"); }
            catch (IOException) { return new(OutputSendStatus.Retry, "wire.disconnected"); }
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private sealed class Session(TestWireOutputTransport owner, string host, int port) : IAsyncDisposable
    {
        private Stream? _stream;
        public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken token)
        {
            try
            {
                // Each real TCP open resolves and checks the actual IP through the product connector.
                _stream ??= await owner._outbound.ConnectAsync(host, port, token);
                var address = Encoding.UTF8.GetBytes(message.Address);
                if (address.Length > OutputTransportRoute.MaximumAddressBytes || message.Payload.Length > StandardEnvelope.MaximumPayloadBytes)
                { throw new IOException("wire.frameTooLarge"); }
                var header = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(header, address.Length);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), message.Payload.Length);
                await _stream.WriteAsync(header, token);
                await _stream.WriteAsync(address, token);
                await _stream.WriteAsync(message.Payload, token);
                var ack = new byte[1];
                await _stream.ReadExactlyAsync(ack, token);
                return ack[0] == 6 ? new(OutputSendStatus.Ack) : new(OutputSendStatus.SuspendTarget, "wire.receiverPaused");
            }
            catch
            {
                if (_stream is not null)
                { await _stream.DisposeAsync(); _stream = null; }
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_stream is not null)
            { await _stream.DisposeAsync(); _stream = null; }
            Interlocked.Increment(ref owner._disposedSessions);
        }
    }
}
