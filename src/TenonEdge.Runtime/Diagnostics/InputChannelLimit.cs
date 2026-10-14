using System.Threading.Channels;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>所有输入共享的每通道条数策略。更新不替换通道或丢弃已有消息；
/// 发布检查与参数切换共用短锁，避免保存返回后仍按旧上限准入。</summary>
public sealed class InputChannelLimit
{
    public const int MaximumCapacity = 10000;
    private readonly Lock _gate = new();
    private int _capacity;
    private TaskCompletionSource _changed = NewSignal();

    public InputChannelLimit(int capacity)
    {
        Validate(capacity);
        _capacity = capacity;
    }
    public int Capacity { get { lock (_gate) { return _capacity; } } }
    internal (int Capacity, Task Changed) Snapshot { get { lock (_gate) { return (_capacity, _changed.Task); } } }

    public static void Validate(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);
    }

    public void Configure(int capacity)
    {
        Validate(capacity);
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (capacity == _capacity)
            { return; }
            changed = _changed;
            _changed = NewSignal();
            _capacity = capacity;
        }
        changed.TrySetResult();
    }

    internal bool TryPublish(Channel<EdgeMessage> channel, EdgeMessage message)
    {
        lock (_gate)
        { return channel.Reader.Count < _capacity && channel.Writer.TryWrite(message); }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
