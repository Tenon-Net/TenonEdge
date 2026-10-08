using System.Net;
using System.Net.Sockets;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusRtuDrainTests
{
    [Fact]
    public async Task SilentSocketCanBeReusedAndNoPendingReadStealsNextResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var result = await ModbusRtuDrain.DrainAsync(client.GetStream(), TimeSpan.FromMilliseconds(100), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(result.CanReuseConnection);
        Assert.Equal(0, result.DiscardedBytes);
        await peer.GetStream().WriteAsync(new byte[] { 0x55 }, TestContext.Current.CancellationToken);
        var buffer = new byte[1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await client.GetStream().ReadExactlyAsync(buffer, deadline.Token);
        Assert.Equal((byte)0x55, buffer[0]);
    }

    [Fact]
    public async Task OldFrameIsDiscardedAndCannotAuthorizeAnotherRequestOnSameConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        // 完整合法 RTU 帧也必须丢弃，不能通过 CRC/长度校验冒充新请求的结果。
        byte[] oldFrame = [1, 3, 4, 0x10, 0, 0x10, 1, 0x32, 0xf3];
        await peer.GetStream().WriteAsync(oldFrame, TestContext.Current.CancellationToken);
        var result = await ModbusRtuDrain.DrainAsync(client.GetStream(), TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(result.CanReuseConnection);
        Assert.Equal(oldFrame.Length, result.DiscardedBytes);
    }
}
