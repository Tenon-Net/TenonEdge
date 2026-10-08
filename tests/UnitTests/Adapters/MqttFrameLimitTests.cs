using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Adapter;
using MQTTnet.Channel;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttFrameLimitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task FragmentedAndCoalescedPacketsPreserveBytesAndDoNotInterpretBodyAsHeader(int fragment)
    {
        byte[] wire = [0xd0, 0, 0x30, 3, 0xff, 0xff, 0xff, 0x32, 1, 0xff, 0xd0, 0];
        using var source = new SourceChannel(wire, fragment);
        using var channel = new MqttFrameLimitChannel(source, 3, () => Assert.Fail("合法帧不能触发超限。"));
        Assert.Equal(wire, await DrainAsync(channel));
    }

    [Theory]
    [InlineData(129)]
    [InlineData(16384)]
    [InlineData(2097152)]
    [InlineData(268435455)]
    public async Task RejectsDeclaredLengthBeforeExposingCompleteHeaderOrReadingBody(int length)
    {
        var header = Header(length);
        using var source = new SourceChannel([.. header, 0xff], 4096);
        var reports = 0;
        using var channel = new MqttFrameLimitChannel(source, 128, () => reports++);
        await Assert.ThrowsAsync<MqttProtocolViolationException>(() => DrainAsync(channel));
        Assert.InRange(source.BytesRead, 2, header.Length);
        var reads = source.Reads;
        await Assert.ThrowsAsync<MqttProtocolViolationException>(() => channel.ReadAsync(new byte[4], 0, 4, Token));
        Assert.Equal(reads, source.Reads);
        Assert.Equal(1, reports);
    }

    [Fact]
    public async Task ExactBoundaryAndFollowingZeroLengthPacketAreAccepted()
    {
        byte[] wire = [.. Header(128), .. new byte[128], 0xd0, 0];
        using var source = new SourceChannel(wire, 4096);
        using var channel = new MqttFrameLimitChannel(source, 128, () => Assert.Fail("边界帧不能被拒绝。"));
        Assert.Equal(wire, await DrainAsync(channel));
    }

    [Fact]
    public async Task LengthEncodingCannotContinuePastFourBytes()
    {
        using var source = new SourceChannel([0x30, 0x80, 0x80, 0x80, 0x80, 0], 1);
        using var channel = new MqttFrameLimitChannel(source, int.MaxValue, () => Assert.Fail("编码错误不是大小超限。"));
        await Assert.ThrowsAsync<MqttProtocolViolationException>(() => DrainAsync(channel));
        Assert.Equal(5, source.BytesRead);
    }

    [Fact]
    public async Task ActualLibraryDecoderNeverRequestsBodyForOversizedHeader()
    {
        var header = Header(MqttBoundedAdapterFactory.MaximumRemainingLength + 1);
        using var source = new SourceChannel(header, 1);
        var reports = 0;
        using var channel = new MqttFrameLimitChannel(source, MqttBoundedAdapterFactory.MaximumRemainingLength, () => reports++);
        using var adapter = new MqttChannelAdapter(channel,
            new MqttPacketFormatterAdapter(MqttProtocolVersion.V311, new MqttBufferWriter(4096, 65536)), new MqttNetNullLogger());
        // MQTTnet 的同步完成路径通过 Task.Result 包装异常，异步路径可能直接传播；根因必须是长度拒绝。
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => adapter.ReceivePacketAsync(Token));
        Assert.IsType<MqttProtocolViolationException>(failure.GetBaseException());
        Assert.Equal(header.Length, source.BytesRead);
        Assert.Equal(0, source.ReadsAtEnd); // 库没有机会开始正文读取；完整 Remaining Length 未交给它。
        Assert.Equal(1, reports);
    }

    [Fact]
    public async Task MaximumValidQosOnePayloadAndTopicStillReachLibraryDecoder()
    {
        var header = Header(MqttBoundedAdapterFactory.MaximumRemainingLength);
        header[0] = 0x32;
        var wire = new byte[header.Length + MqttBoundedAdapterFactory.MaximumRemainingLength];
        header.CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(header.Length, 2), ushort.MaxValue);
        wire.AsSpan(header.Length + 2, ushort.MaxValue).Fill((byte)'a');
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(header.Length + 2 + ushort.MaxValue, 2), 1);
        using var source = new SourceChannel(wire, 4096);
        using var channel = new MqttFrameLimitChannel(source, MqttBoundedAdapterFactory.MaximumRemainingLength,
            () => Assert.Fail("完整 1 MiB 载荷加最长 Topic 必须被接收。"));
        using var adapter = new MqttChannelAdapter(channel,
            new MqttPacketFormatterAdapter(MqttProtocolVersion.V311, new MqttBufferWriter(4096, 65536)), new MqttNetNullLogger());
        var packet = Assert.IsType<MqttPublishPacket>(await adapter.ReceivePacketAsync(Token));
        Assert.Equal(ushort.MaxValue, packet.Topic.Length);
        Assert.Equal(1, packet.PacketIdentifier);
        Assert.Equal(MqttInputReceiver.MaximumPayloadBytes, packet.Payload.Length);
    }

    [Fact]
    public async Task WritesAndTransportMetadataAreForwardedAndCanceledReadDoesNotTouchTransport()
    {
        using var source = new SourceChannel([], 1);
        using var channel = new MqttFrameLimitChannel(source, 128, () => { });
        await channel.ConnectAsync(Token);
        byte[] wire = [0xc0, 0];
        await channel.WriteAsync(new ReadOnlySequence<byte>(wire), true, Token);
        Assert.Equal(wire, source.Written);
        Assert.True(source.EndOfPacket);
        Assert.Same(source.RemoteEndPoint, channel.RemoteEndPoint);
        Assert.Equal(source.IsSecureConnection, channel.IsSecureConnection);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(new byte[4], 0, 4, canceled.Token));
        Assert.Equal(0, source.Reads);
        await channel.DisconnectAsync(Token);
        Assert.True(source.Disconnected);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Header(int length)
    {
        var bytes = new List<byte> { 0x30 };
        do
        {
            var current = length % 128;
            length /= 128;
            bytes.Add((byte)(current | (length > 0 ? 0x80 : 0)));
        } while (length > 0);
        return bytes.ToArray();
    }
    private static async Task<byte[]> DrainAsync(MqttFrameLimitChannel channel)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32];
        int read;
        while ((read = await channel.ReadAsync(buffer, 3, 25, Token)) != 0)
        { output.Write(buffer, 3, read); }
        return output.ToArray();
    }
    private sealed class SourceChannel(byte[] wire, int fragment) : IMqttChannel
    {
        public int BytesRead { get; private set; }
        public int Reads { get; private set; }
        public int ReadsAtEnd { get; private set; }
        public byte[] Written { get; private set; } = [];
        public bool EndOfPacket { get; private set; }
        public bool Disconnected { get; private set; }
        public X509Certificate2 ClientCertificate => null!;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1883);
        public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 12345);
        public bool IsSecureConnection => true;
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) { Disconnected = true; return Task.CompletedTask; }
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reads++;
            if (BytesRead == wire.Length)
            { ReadsAtEnd++; }
            var read = Math.Min(Math.Min(count, fragment), wire.Length - BytesRead);
            wire.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            BytesRead += read;
            return Task.FromResult(read);
        }
        public Task WriteAsync(ReadOnlySequence<byte> buffer, bool isEndOfPacket, CancellationToken cancellationToken)
        { Written = buffer.ToArray(); EndOfPacket = isEndOfPacket; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
