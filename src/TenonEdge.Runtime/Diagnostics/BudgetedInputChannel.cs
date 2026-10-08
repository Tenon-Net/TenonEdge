using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 输入占用共享预算直到分发提交。输出在输入入队前预留最坏报文字节，避免所有输入
/// 占满预算后，分发器又互相等待输出额度而死锁。无输出时普通 Reader 取走即释放。
/// </summary>
internal sealed class BudgetedInputChannel : Channel<EdgeMessage>
{
    private readonly Channel<EdgeMessage> _inner;
    private readonly InFlightBudget _budget;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _completionError;
    internal long OutputReservationBytes { get; set; }
    internal bool ReserveSerializationWorkspace { get; set; }

    public BudgetedInputChannel(int capacity, InFlightBudget budget)
    {
        _budget = budget;
        _inner = Channel.CreateBounded<EdgeMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        Writer = new BudgetWriter(this);
        Reader = new BudgetReader(this);
    }

    internal ChannelReader<EdgeMessage> HoldingReader => _inner.Reader;
    private long Charge(long sourceBytes) => checked(sourceBytes + OutputReservationBytes
        + (ReserveSerializationWorkspace ? StandardEnvelope.SerializationReservationBytes(sourceBytes) : 0));
    internal long Charge(EdgeMessage message) => Charge(message.BudgetBytes);
    internal void Release(EdgeMessage message) => _budget.Release(Charge(message));

    private sealed class BudgetWriter(BudgetedInputChannel owner) : InputMessageWriter
    {
        // 通道声明 SingleWriter；失败的 TryWrite 记住所需大小，随后等待不会因“仅有通道空位”而忙转。
        private long _requiredBytes = 1;
        public override async ValueTask ProduceAsync(long maximumBytes,
            Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(produce);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var reserved = owner.Charge(maximumBytes);
            await ReserveAsync(reserved, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner._closed.Task.IsCompleted) { throw new ChannelClosedException(owner._completionError); }
                var message = await produce(cancellationToken);
                if (message is null) { return; }
                var charge = owner.Charge(message);
                if (charge > reserved) { throw new InputMessageSizeException("实际输入消息超过预先声明的字节预算。"); }
                if (charge < reserved)
                {
                    owner._budget.Release(reserved - charge);
                    reserved = charge;
                }

                await owner._inner.Writer.WriteAsync(message, cancellationToken);
                reserved = 0; // 所有权移交给通道/分发器，直到存储接管或明确拒绝。
            }
            finally
            {
                if (reserved > 0) { owner._budget.Release(reserved); }
            }
        }

        public override bool TryComplete(Exception? error = null)
        {
            if (!owner._inner.Writer.TryComplete(error)) { return false; }
            owner._completionError = error;
            owner._closed.TrySetResult();
            return true;
        }

        public override bool TryWrite(EdgeMessage item)
        {
            var bytes = owner.Charge(item);
            ValidateSize(bytes);
            _requiredBytes = bytes;
            if (!owner._budget.TryReserve(bytes)) { return false; }
            if (owner._inner.Writer.TryWrite(item)) { return true; }
            owner._budget.Release(bytes);
            return false;
        }

        public override async ValueTask WriteAsync(EdgeMessage item, CancellationToken cancellationToken = default)
        {
            var bytes = owner.Charge(item);
            await ReserveAsync(bytes, cancellationToken);
            try { await owner._inner.Writer.WriteAsync(item, cancellationToken); }
            catch { owner._budget.Release(bytes); throw; }
        }

        private void ValidateSize(long bytes)
        {
            if (bytes > owner._budget.Capacity) { throw new InputMessageSizeException("输入消息及输出预留超过全局在途预算。"); }
        }

        private ValueTask ReserveAsync(long bytes, CancellationToken cancellationToken) =>
            WaitForBudgetAsync(bytes, reserve: true, cancellationToken);

        private async ValueTask WaitForBudgetAsync(long bytes, bool reserve, CancellationToken cancellationToken)
        {
            ValidateSize(bytes);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var reservation = reserve ? owner._budget.ReserveAsync(bytes, stop.Token).AsTask()
                : owner._budget.WaitForAvailableAsync(bytes, stop.Token).AsTask();
            if (await Task.WhenAny(reservation, owner._closed.Task) == owner._closed.Task)
            {
                await stop.CancelAsync();
            }

            try { await reservation; }
            catch (OperationCanceledException) when (owner._closed.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                throw new ChannelClosedException(owner._completionError);
            }
        }

        public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        {
            if (!await owner._inner.Writer.WaitToWriteAsync(cancellationToken)) { return false; }
            try { await WaitForBudgetAsync(_requiredBytes, reserve: false, cancellationToken); }
            catch (ChannelClosedException) when (owner._closed.Task.IsCompleted)
            {
                // 保持 ChannelWriter 的完成语义：正常关闭为 false，异常关闭传播原因。
            }

            return await owner._inner.Writer.WaitToWriteAsync(cancellationToken);
        }
    }

    private sealed class BudgetReader(BudgetedInputChannel owner) : ChannelReader<EdgeMessage>
    {
        public override bool CanCount => owner._inner.Reader.CanCount;
        public override int Count => owner._inner.Reader.Count;
        public override Task Completion => owner._inner.Reader.Completion;
        public override bool TryRead([MaybeNullWhen(false)] out EdgeMessage item)
        {
            if (!owner._inner.Reader.TryRead(out item)) { return false; }
            owner.Release(item);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            owner._inner.Reader.WaitToReadAsync(cancellationToken);
    }
}
