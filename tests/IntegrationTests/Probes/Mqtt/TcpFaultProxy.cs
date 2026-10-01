using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 的故障代理（验证代码）：在客户端与 Broker 之间转发 TCP 数据。<see cref="Freeze"/> 之后双向停止转发，但不关闭任何连接，
/// 也不把对端的关闭传给另一端，模拟“拔网线”：客户端的写入照常成功（进入发送缓冲），却收不到任何数据。
/// 冻结期间读到的数据先扣住，<see cref="Thaw"/> 之后再送达，与 TCP 在网线接回后重传的效果相同。
/// </summary>
internal sealed class TcpFaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Socket> _sockets = [];
    private readonly ConcurrentBag<Task> _pumps = [];
    private readonly Lock _lock = new();
    private readonly Task _acceptLoop;
    private TaskCompletionSource _thawed = CreateOpenGate();

    private TcpFaultProxy(IPEndPoint target)
    {
        _target = target;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptAsync(_stopping.Token);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static TcpFaultProxy Start(int targetPort) => new(new IPEndPoint(IPAddress.Loopback, targetPort));

    public void Freeze()
    {
        lock (_lock)
        {
            if (_thawed.Task.IsCompleted)
            {
                _thawed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Thaw()
    {
        lock (_lock)
        {
            _thawed.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        Thaw();
        _listener.Stop();
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        try
        {
            await Task.WhenAll([_acceptLoop, .. _pumps]);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 停止时套接字被关闭，循环以这些异常结束属于正常情况。
        }

        _listener.Dispose();
        _stopping.Dispose();
    }

    private static TaskCompletionSource CreateOpenGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private Task WaitWhileFrozenAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _thawed.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptSocketAsync(cancellationToken);
            var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
            _sockets.Add(client);
            _sockets.Add(upstream);
            await upstream.ConnectAsync(_target, cancellationToken);
            _pumps.Add(PumpAsync(client, upstream, cancellationToken));
            _pumps.Add(PumpAsync(upstream, client, cancellationToken));
        }
    }

    private async Task PumpAsync(Socket source, Socket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            await WaitWhileFrozenAsync(cancellationToken);
            var received = await source.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

            // 冻结期间读到的数据或对端的关闭都先扣住，解冻后再处理。
            await WaitWhileFrozenAsync(cancellationToken);
            if (received == 0)
            {
                destination.Shutdown(SocketShutdown.Send);
                return;
            }

            await destination.SendAsync(buffer.AsMemory(0, received), SocketFlags.None, cancellationToken);
        }
    }
}
