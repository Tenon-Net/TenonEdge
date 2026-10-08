using System.Net;
using System.Net.Sockets;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusRequestExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingConnectionInterruptsSynchronousNetworkReadBeforeExecutorIsReleased(bool dispose)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient { ReceiveTimeout = 10000 };
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using var executor = new ModbusRequestExecutor();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = 0;
        var read = executor.ExecuteAsync(() =>
        {
            var stream = client.GetStream();
            entered.SetResult();
            try
            { return stream.ReadByte(); }
            finally { Interlocked.Exchange(ref exited, 1); }
        }, client.Dispose, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (dispose)
        { await executor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); }
        else
        { await cancellation.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref exited));
        if (!dispose)
        { Assert.Equal(42, await executor.ExecuteAsync(() => 42, () => { }, TestContext.Current.CancellationToken)); }
    }
}
