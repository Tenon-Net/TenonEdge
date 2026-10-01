using System.Diagnostics;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>T0-12 比较测试共用的常量与辅助方法。</summary>
internal static class ModbusProbe
{
    /// <summary>比较时统一使用的读取超时。</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>超过读取超时、用来制造迟到应答的延迟。</summary>
    public static readonly TimeSpan LateDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>计时断言的余量，留给机器负载。</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1500);

    /// <summary>等待某件事发生的上限，超过即判测试失败。</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>判断“一直没有返回”时观察的时长：远大于读取超时加余量。</summary>
    public static readonly TimeSpan HangObservation = TimeSpan.FromSeconds(2);

    public static TheoryData<ModbusLibrary, ModbusFraming> AllCombinations() => new()
    {
        { ModbusLibrary.NModbus, ModbusFraming.Tcp },
        { ModbusLibrary.NModbus, ModbusFraming.RtuOverTcp },
        { ModbusLibrary.FluentModbus, ModbusFraming.Tcp },
        { ModbusLibrary.FluentModbus, ModbusFraming.RtuOverTcp },
    };

    /// <summary>在专用线程上执行同步读取（总设计 §6.1：每个连接循环使用专用线程），调用方据此观察它何时结束。</summary>
    public static Task<ushort[]> ReadOnDedicatedThread(ModbusProbeClient client, ushort address, ushort count = 2) =>
        Task.Factory.StartNew(
            () => client.Read(1, address, count),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>执行同步读取，返回异常（没有异常时为空）与耗时。</summary>
    public static (Exception? Exception, TimeSpan Elapsed) MeasureRead(Func<ushort[]> read)
    {
        var stopwatch = Stopwatch.StartNew();
        var exception = Record.Exception(() => read());
        return (exception, stopwatch.Elapsed);
    }

    public static async Task<(Exception? Exception, TimeSpan Elapsed)> MeasureAsync(Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(operation);
        return (exception, stopwatch.Elapsed);
    }

    public static string Describe(Exception? exception) =>
        exception is null ? "无异常" : $"{exception.GetType().FullName}：{exception.Message}"
            + (exception.InnerException is { } inner ? $"（内部 {inner.GetType().FullName}：{inner.Message}）" : string.Empty);
}
