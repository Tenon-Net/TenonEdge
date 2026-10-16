using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>运行时测试的公共装配：每个运行时使用自己的临时目录中的 queue.db 与记录事件的替身。</summary>
internal static class RuntimeTesting
{
    internal static async Task<TenonEdge.Abstractions.Messages.EdgeMessage> ReadCapturedAsync(
        RuntimeHost runtime, TenonEdge.Abstractions.Plans.RuntimeUnitId id, long afterSequence = 0)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var record = runtime.Messages.Read(id, false).LastOrDefault(item => item.Sequence > afterSequence);
            if (record is not null)
            {
                Assert.True(runtime.Messages.TryReadComplete(id, record.Sequence, record.Revision, record.SourceMessageId, out var bytes));
                return InputMessageSnapshot.Decode(bytes);
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    public const string ConfigStoreId = "00000000-0000-4000-8000-000000000001";

    public static DeliveryCache CreateDeliveryCache(string directory, TimeProvider? time = null, Func<DeliveryCacheOptions, DeliveryCacheOptions>? configure = null)
    {
        var options = new DeliveryCacheOptions
        {
            DatabasePath = Path.Combine(directory, DeliveryCacheOptions.FileName),
            ConfigStoreId = ConfigStoreId,
            CommitInterval = TimeSpan.FromMilliseconds(5),
        };
        return new DeliveryCache(configure?.Invoke(options) ?? options, time ?? TimeProvider.System);
    }

    public static RuntimeHost CreateRuntime(
        IEnumerable<IInputUnitFactory> factories,
        string directory,
        RecordingEventSink? events = null,
        TimeProvider? time = null,
        RuntimeOptions? options = null,
        IClockSyncStatus? clock = null,
        Func<DeliveryCacheOptions, DeliveryCacheOptions>? configureCache = null,
        IEnumerable<IOutputAdapterFactory>? outputs = null,
        IEnumerable<IRuntimePlanAdapterProvider>? planAdapters = null,
        ILogger<RuntimeHost>? logger = null, TenonEdge.Runtime.Transforms.TransformCatalog? transforms = null)
    {
        time ??= TimeProvider.System;
        return new RuntimeHost(
            factories,
            CreateDeliveryCache(directory, time, configureCache),
            clock ?? new UnknownClockSyncStatus(),
            events ?? new RecordingEventSink(),
            time,
            options ?? new RuntimeOptions { InitialRestartDelay = TimeSpan.FromMilliseconds(50), RestartJitter = 0 },
            logger ?? NullLogger<RuntimeHost>.Instance,
            random: () => 0,
            outputFactories: outputs, planAdapterProviders: planAdapters, transforms: transforms);
    }
}

/// <summary>记录运行事件的替身；可在记录时回调（例如在公布故障的那一刻推进可控时钟）。</summary>
internal sealed class RecordingEventSink(Action<RecordedRuntimeEvent>? onRecorded = null) : IRuntimeEventSink
{
    private readonly ConcurrentQueue<RecordedRuntimeEvent> _events = new();

    public IReadOnlyList<RecordedRuntimeEvent> Events => [.. _events];

    public void Record(RuntimeEventSeverity severity, string type, string? target, string description)
    {
        var recorded = new RecordedRuntimeEvent(severity, type, target, description);
        _events.Enqueue(recorded);
        onRecorded?.Invoke(recorded);
    }

    public IReadOnlyList<RecordedRuntimeEvent> OfType(string type) => [.. _events.Where(recorded => recorded.Type == type)];
}

internal sealed record RecordedRuntimeEvent(RuntimeEventSeverity Severity, string Type, string? Target, string Description);

/// <summary>固定状态的时钟同步状态替身。</summary>
internal sealed class FixedClockSyncStatus(ClockSyncState state) : IClockSyncStatus
{
    public ClockSyncState State { get; set; } = state;
}
