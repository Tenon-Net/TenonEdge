using Microsoft.Extensions.Time.Testing;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusReadClientTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DecodesPackedCoilsAndDiscreteInputs(byte function)
    {
        using var stream = new WireStream([0, 1, 0, 0, 0, 5, 1, function, 2, 0x85, 1]);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.Tcp, TimeProvider.System);
        Assert.Equal("True,False,True,False,False,False,False,True,True",
            string.Join(',', client.ReadBits(1, function, 0, 9, TimeSpan.FromSeconds(1))));
        Assert.Equal(12, stream.Written);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 5)]
    [InlineData(0, 255)]
    public void RejectsInvalidMbapBeforeReadingAdvertisedBody(byte protocol, byte length)
    {
        using var stream = new WireStream([0, 1, 0, protocol, 0, length]);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.Tcp, TimeProvider.System);
        Assert.Throws<IOException>(() => client.ReadRegisters(1, 3, 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(6, stream.Consumed);
    }

    [Fact]
    public void FragmentedReadsShareOneDeadlineInsteadOfRestartingTimeoutPerByte()
    {
        var time = new FakeTimeProvider();
        using var stream = new WireStream([0, 1, 0, 0, 0, 5, 1, 3, 2, 0x12, 0x34], time);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.Tcp, time);
        Assert.Throws<TimeoutException>(() => client.ReadRegisters(1, 3, 0, 1, TimeSpan.FromMilliseconds(35)));
        Assert.Equal(4, stream.Consumed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void TcpPartialHeaderOrBodySurvivesTimeoutAndIsSkippedByNextRequest(int timeoutAfter)
    {
        using var stream = new WireStream(
            [0, 1, 0, 0, 0, 5, 1, 3, 2, 0x12, 0x34, 0, 2, 0, 0, 0, 5, 2, 4, 2, 0x56, 0x78],
            timeoutAfter: timeoutAfter);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.Tcp, TimeProvider.System);
        Assert.Throws<TimeoutException>(() => client.ReadRegisters(1, 3, 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(timeoutAfter, stream.Consumed);
        Assert.Equal((ushort)0x5678, Assert.Single(client.ReadRegisters(2, 4, 0, 1, TimeSpan.FromSeconds(1))));
        Assert.False(client.NeedsNewConnection);
        Assert.Equal(24, stream.Written);
    }

    [Fact]
    public void TcpTransactionSpaceRequiresNewConnectionBeforeReuse()
    {
        using var stream = new WireStream([]);
        using var resource = new ModbusReadStream(stream, ModbusFrameFormat.Tcp, TimeProvider.System);
        resource.Begin(TimeSpan.FromSeconds(1));
        resource.Write([255, 255, 0, 0, 0, 6, 1, 3, 0, 0, 0, 1], 0, 12);
        Assert.True(resource.NeedsNewConnection);
        Assert.Throws<IOException>(() => resource.Begin(TimeSpan.FromSeconds(1)));
        Assert.Equal(12, stream.Written);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    [InlineData(255)]
    public void RtuRejectsReservedUnitsBeforeWriting(byte unit)
    {
        using var stream = new WireStream([]);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.RtuOverTcp, TimeProvider.System);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadRegisters(unit, 3, 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, stream.Written);
    }

    [Fact]
    public void InvalidRangeAndWrongFunctionFailBeforeWriting()
    {
        using var stream = new WireStream([]);
        using var client = new ModbusReadClient(stream, ModbusFrameFormat.Tcp, TimeProvider.System);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadRegisters(1, 3, 65535, 2, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadRegisters(1, 3, 0, 126, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadBits(1, 1, 0, 2001, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ReadRegisters(1, 6, 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, stream.Written);
    }

    private sealed class WireStream(byte[] response, FakeTimeProvider? time = null, int? timeoutAfter = null) : Stream
    {
        private bool _timedOut;
        public int Consumed { get; private set; }
        public int Written { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override bool CanTimeout => true;
        public override int ReadTimeout { get; set; }
        public override int WriteTimeout { get; set; }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_timedOut && Consumed == timeoutAfter)
            { _timedOut = true; throw new TimeoutException("injected partial frame"); }
            var take = Math.Min(Math.Min(count, response.Length - Consumed), time is null && timeoutAfter is null ? int.MaxValue : 1);
            response.AsSpan(Consumed, take).CopyTo(buffer.AsSpan(offset, count));
            Consumed += take;
            time?.Advance(TimeSpan.FromMilliseconds(10));
            return take;
        }
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
