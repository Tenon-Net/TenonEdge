using System.Runtime.InteropServices;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>
/// 读取 Linux 内核校时状态，不依赖容器中的 systemd、D-Bus 或外部命令。
/// modes 始终为零，只查询、不校时；返回的是内核同步状态，不保证实际时间准确。
/// 仅使用已核对的 Linux x64 timex ABI；其他平台或无法读取时明确返回未知。
/// </summary>
internal sealed class ClockSyncStatus(TimeProvider time, IRuntimeEventSink events, Func<int> probe) : IClockSyncStatus
{
    private readonly Lock _lock = new();
    private bool _sampled;
    private long _sampleTimestamp;
    private ClockSyncState _state;

    public ClockSyncState State
    {
        get
        {
            lock (_lock)
            {
                if (_sampled && time.GetElapsedTime(_sampleTimestamp) < TimeSpan.FromSeconds(10))
                {
                    return _state;
                }

                var state = Classify(probe());
                if (!_sampled || state != _state)
                {
                    events.Record(state == ClockSyncState.Synchronized ? RuntimeEventSeverity.Info : RuntimeEventSeverity.Warning,
                        "clock.synchronization", "system", state switch
                        {
                            ClockSyncState.Synchronized => "内核报告系统时钟已同步。",
                            ClockSyncState.NotSynchronized => "内核报告系统时钟未同步，网关时间不可信。",
                            _ => "无法确定系统时钟同步状态。",
                        });
                }

                _state = state;
                _sampleTimestamp = time.GetTimestamp();
                _sampled = true;
                return state;
            }
        }
    }

    // Linux TIME_OK、TIME_INS、TIME_DEL、TIME_OOP、TIME_WAIT 均非同步错误；TIME_ERROR=5。
    internal static ClockSyncState Classify(int result) => result switch
    {
        >= 0 and <= 4 => ClockSyncState.Synchronized,
        5 => ClockSyncState.NotSynchronized,
        _ => ClockSyncState.Unknown,
    };

    internal static int ReadKernel()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return -1;
        }

        try
        {
            var query = new TimexQuery();
            return Adjtimex(ref query);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return -1;
        }
    }

    // Linux x64 struct timex 为 208 字节，首字段 unsigned int modes；其余只供内核写入。
    // 使用显式大小的零初始化值，避免未初始化的 modes 意外请求修改系统时钟。
    [StructLayout(LayoutKind.Explicit, Size = 208)]
    private struct TimexQuery
    {
        [FieldOffset(0)]
        public uint Modes;
    }

    [DllImport("libc", EntryPoint = "adjtimex", ExactSpelling = true)]
    private static extern int Adjtimex(ref TimexQuery query);
}
