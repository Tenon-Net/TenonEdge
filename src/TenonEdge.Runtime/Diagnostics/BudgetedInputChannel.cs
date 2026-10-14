using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 输入占用共享预算直到分发提交。输出在输入入队前预留最坏报文字节，避免所有输入
/// 占满预算后，分发器又互相等待输出额度而死锁。无输出时普通 Reader 取走即释放。
/// </summary>
internal sealed class BudgetedInputChannel : Channel<EdgeMessage>
{
    private readonly Channel<EdgeMessage> _inner;
    private readonly InFlightBudget _budget;
    private readonly int _capacity;
    private readonly Action<EdgeMessage>? _capture;
    private readonly Lock _captureGate = new();
    private long _captureFailures;
    internal long CaptureFailures => Interlocked.Read(ref _captureFailures);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _completionError;
    private readonly ConcurrentDictionary<EdgeMessage, TaskCompletionSource<InputHandoffStatus>> _handoffs = new(ReferenceEqualityComparer.Instance);
    internal bool UsesDurableHandoff { get; init; }
    internal long OutputReservationBytes { get; set; }
    internal bool ReserveSerializationWorkspace { get; set; }
    internal long MinimumSerializationReservationBytes { get; set; }
    internal long MappingWorkingReservationBytes { get; set; }

    public BudgetedInputChannel(int capacity, InFlightBudget budget, Action<EdgeMessage>? capture = null)
    {
        _capture = capture;
        _budget = budget;
        _capacity = capacity;
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
        + (UsesDurableHandoff ? 1024 : 0) // 回执、异步等待与索引项的保守固定预约。
        + MappingWorkingReservationBytes
        + (ReserveSerializationWorkspace ? Math.Max(MinimumSerializationReservationBytes, StandardEnvelope.SerializationReservationBytes(MappingWorkingReservationBytes > 0 ? FieldMapping.MaximumResultBytes : sourceBytes)) : 0));
    internal long Charge(EdgeMessage message) => Charge(message.BudgetBytes);
    internal bool HasHandoff(EdgeMessage message) => _handoffs.ContainsKey(message);
    internal void Release(EdgeMessage message, InputHandoffStatus status = InputHandoffStatus.Stopped)
    {
        lock (_captureGate)
        {
            _budget.Release(Charge(message));
            if (_handoffs.TryRemove(message, out var completion))
            { completion.TrySetResult(status); }
        }
    }

    // 发布和诊断捕获期间禁止读取方释放消息预算；分发器仍可并行处理不可变消息。
    // 所有写入路径都经过这里，满队列/取消/关闭不产生伪捕获，分发重试也不重复捕获。
    private bool TryPublish(EdgeMessage message)
    {
        lock (_captureGate)
        {
            if (!_inner.Writer.TryWrite(message))
            { return false; }
            try
            { _capture?.Invoke(message); }
            // 消息已经发布；包括诊断分配失败在内，均不能把业务写入改判失败或提前释放预算。
            catch (Exception)
            { Interlocked.Increment(ref _captureFailures); }
            return true;
        }
    }

    private async ValueTask PublishAsync(EdgeMessage message, CancellationToken cancellationToken)
    {
        while (await _inner.Writer.WaitToWriteAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryPublish(message))
            { return; }
        }
        throw new ChannelClosedException(_completionError);
    }

    private sealed class BudgetWriter(BudgetedInputChannel owner) : InputMessageWriter
    {
        // 通道声明 SingleWriter；失败的 TryWrite 记住所需大小，随后等待不会因“仅有通道空位”而忙转。
        private long _requiredBytes = 1;
        public override ValueTask ProduceAsync(long maximumBytes,
            Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default) =>
            ProduceCoreAsync(maximumBytes, maximumBytes, produce, false, null, cancellationToken);

        public override ValueTask ProduceAsync(long maximumWorkingBytes, long maximumMessageBytes,
            Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default) =>
            ProduceCoreAsync(maximumWorkingBytes, maximumMessageBytes, produce, true, null, cancellationToken);

        public override bool TryProduce(long maximumWorkingBytes, long maximumMessageBytes,
            Func<EdgeMessage?> produce, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(produce);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumWorkingBytes, maximumMessageBytes);
            if (maximumWorkingBytes > owner._budget.WorkingCapacity)
            { throw new InputMessageSizeException("输入解析工作超过全局在途预算。"); }
            cancellationToken.ThrowIfCancellationRequested();
            if (owner._closed.Task.IsCompleted)
            { throw new ChannelClosedException(owner._completionError); }
            // 单写者：检查后只有读取者能减少数量，不在满通道时先分配正文或解析树。
            if (owner._inner.Reader.Count >= owner._capacity)
            { return false; }
            var reserved = Math.Max(maximumWorkingBytes, Math.Min(owner.Charge(maximumMessageBytes), owner._budget.WorkingCapacity));
            if (!owner._budget.TryReserve(reserved))
            { return false; }
            try
            {
                var message = produce();
                if (message is null)
                { return true; }
                cancellationToken.ThrowIfCancellationRequested();
                if (message.BudgetBytes > maximumMessageBytes)
                { throw new InputMessageSizeException("输入消息超过结果大小上限。"); }
                var charge = owner.Charge(message);
                if (charge > reserved)
                { throw new InputMessageSizeException("实际输入消息超过预先声明的字节预算。"); }
                if (reserved > charge)
                { owner._budget.Release(reserved - charge); }
                reserved = charge;
                if (!owner.TryPublish(message))
                { return false; }
                reserved = 0;
                return true;
            }
            finally
            {
                if (reserved > 0)
                { owner._budget.Release(reserved); }
            }
        }

        public override async ValueTask<InputHandoffStatus> ProduceConfirmedAsync(long maximumWorkingBytes, long maximumMessageBytes,
            Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default)
        {
            if (!owner.UsesDurableHandoff)
            { throw new InvalidOperationException("输入工厂未声明持久接管契约。"); }
            var completion = new TaskCompletionSource<InputHandoffStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            await ProduceCoreAsync(maximumWorkingBytes, maximumMessageBytes, produce, true, completion, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }

        private async ValueTask ProduceCoreAsync(long maximumWorkingBytes, long maximumMessageBytes,
            Func<CancellationToken, ValueTask<EdgeMessage?>> produce, bool capOutputPhase, TaskCompletionSource<InputHandoffStatus>? completion, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(produce);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumWorkingBytes, maximumMessageBytes);
            if (maximumWorkingBytes > owner._budget.WorkingCapacity)
            { throw new InputMessageSizeException("输入解析工作超过全局在途预算。"); }
            var outputPhase = owner.Charge(maximumMessageBytes);
            // 双上限允许保守结果上界超过总额度；预约总额度后按实际消息复核，而非拒绝所有小报文。
            var reserved = Math.Max(maximumWorkingBytes, capOutputPhase ? Math.Min(outputPhase, owner._budget.WorkingCapacity) : outputPhase);
            await ReserveAsync(reserved, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner._closed.Task.IsCompleted)
                { throw new ChannelClosedException(owner._completionError); }
                var message = await produce(cancellationToken);
                if (message is null)
                { completion?.TrySetResult(InputHandoffStatus.Rejected); return; }
                if (message.BudgetBytes > maximumMessageBytes)
                { throw new InputMessageSizeException("输入消息超过结果大小上限。"); }
                var charge = owner.Charge(message);
                if (charge > reserved)
                { throw new InputMessageSizeException("实际输入消息超过预先声明的字节预算。"); }
                if (charge < reserved)
                {
                    owner._budget.Release(reserved - charge);
                    reserved = charge;
                }

                if (completion is not null && !owner._handoffs.TryAdd(message, completion))
                { throw new InvalidOperationException("同一消息对象不能同时等待两次接管。"); }
                try
                { await owner.PublishAsync(message, cancellationToken); }
                catch
                {
                    if (completion is not null)
                    { owner._handoffs.TryRemove(message, out _); }
                    throw;
                }
                reserved = 0; // 所有权移交给通道/分发器，直到存储接管或明确拒绝。
            }
            finally
            {
                if (reserved > 0)
                { owner._budget.Release(reserved); }
            }
        }

        public override bool TryComplete(Exception? error = null)
        {
            if (!owner._inner.Writer.TryComplete(error))
            { return false; }
            owner._completionError = error;
            owner._closed.TrySetResult();
            return true;
        }

        public override bool TryWrite(EdgeMessage item)
        {
            var bytes = owner.Charge(item);
            ValidateSize(bytes);
            _requiredBytes = bytes;
            if (!owner._budget.TryReserve(bytes))
            { return false; }
            if (owner.TryPublish(item))
            { return true; }
            owner._budget.Release(bytes);
            return false;
        }

        public override async ValueTask WriteAsync(EdgeMessage item, CancellationToken cancellationToken = default)
        {
            var bytes = owner.Charge(item);
            await ReserveAsync(bytes, cancellationToken);
            try
            { await owner.PublishAsync(item, cancellationToken); }
            catch { owner._budget.Release(bytes); throw; }
        }

        private void ValidateSize(long bytes)
        {
            if (bytes > owner._budget.WorkingCapacity)
            { throw new InputMessageSizeException("输入消息及输出预留超过全局在途预算。"); }
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

            try
            { await reservation; }
            catch (ArgumentOutOfRangeException)
            { throw new InputMessageSizeException("运行参数降低后，本条消息超过新的在途字节预算。"); }
            catch (OperationCanceledException) when (owner._closed.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                throw new ChannelClosedException(owner._completionError);
            }
        }

        public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        {
            if (!await owner._inner.Writer.WaitToWriteAsync(cancellationToken))
            { return false; }
            try
            { await WaitForBudgetAsync(_requiredBytes, reserve: false, cancellationToken); }
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
            if (!owner._inner.Reader.TryRead(out item))
            { return false; }
            owner.Release(item);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            owner._inner.Reader.WaitToReadAsync(cancellationToken);
    }
}
