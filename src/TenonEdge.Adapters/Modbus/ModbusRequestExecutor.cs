using System.Threading.Channels;

namespace TenonEdge.Adapters.Modbus;

/// <summary>一个物理连接的同步协议 I/O 专用线程，不占用线程池执行阻塞读取。
/// read 与 abort 必须捕获同一代已创建连接，read 不得自行重建连接。
/// 传入的 abort 只关闭本次请求使用的连接，不得阻塞；取消不等于完成，实际读取退出前不运行下一项。
/// 队列仅容纳一个候选请求；共享连接的租约串行器负责上层准入。</summary>
internal sealed class ModbusRequestExecutor : IAsyncDisposable
{
    private readonly Channel<IRequest> _requests = Channel.CreateBounded<IRequest>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeLock = new();
    private Task? _disposeTask;

    internal ModbusRequestExecutor()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "TenonEdge.Modbus" };
        worker.Start();
    }

    internal async Task<T> ExecuteAsync<T>(Func<T> read, Action abort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(abort);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Request<T>(read, abort, cancellationToken);
        try
        { await _requests.Writer.WriteAsync(request, cancellationToken); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(ModbusRequestExecutor)); }
        return await request.Completion;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        { return new ValueTask(_disposeTask ??= StopAsync()); }
    }

    private async Task StopAsync()
    {
        _requests.Writer.TryComplete();
        var cancellation = _stopping.CancelAsync();
        // 不能只等取消发出；读取和取消回调都真正退出后才允许共享连接标识释放。
        await _finished.Task;
        await cancellation;
        _stopping.Dispose();
    }

    private void Run()
    {
        try
        {
            while (_requests.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_requests.Reader.TryRead(out var request))
                { request.Run(_stopping.Token); }
            }
        }
        finally { _finished.TrySetResult(); }
    }

    private interface IRequest
    {
        void Run(CancellationToken stoppingToken);
    }

    private sealed class Request<T>(Func<T> read, Action abort, CancellationToken requestToken) : IRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Completion => _completion.Task;

        public void Run(CancellationToken stoppingToken)
        {
            try
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(requestToken, stoppingToken);
                operation.Token.ThrowIfCancellationRequested();
                T value;
                // 注册仅属于当前正在执行的请求；排队取消不能关闭另一个请求的连接。
                // Dispose 等待正在执行的 abort 回调退出，之后才完成请求并启动下一项。
                using (operation.Token.Register(abort))
                {
                    operation.Token.ThrowIfCancellationRequested();
                    value = read();
                }
                operation.Token.ThrowIfCancellationRequested();
                _completion.TrySetResult(value);
            }
            catch (Exception exception)
            {
                if (requestToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
                { _completion.TrySetCanceled(requestToken.IsCancellationRequested ? requestToken : stoppingToken); }
                else
                { _completion.TrySetException(exception); }
            }
        }
    }
}
