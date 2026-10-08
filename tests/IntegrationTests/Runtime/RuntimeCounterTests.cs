using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeCounterTests
{
    [Fact]
    public async Task MqttProtocolCountersAreBoundedAndPersistThroughTheExistingWriter()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            for (var i = 0; i < 100; i++)
            { runtime.Deliveries.RecordMqttInputDiagnostic(1, "acknowledged"); }
            runtime.Deliveries.RecordMqttInputDiagnostic(1, "retainedIgnored");
            runtime.Deliveries.RecordMqttInputDiagnostic(1, "wireOversized");
            Assert.Throws<ArgumentException>(() => runtime.Deliveries.RecordMqttInputDiagnostic(1, "unbounded-secret-value"));
            Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Deliveries.RecordMqttInputDiagnostic(0, "acknowledged"));
            await runtime.StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(100, runtime.Deliveries.GetPersistentCounters()["connection/1/mqtt/acknowledged"]);
            Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["connection/1/mqtt/retainedIgnored"]);
            Assert.Empty(runtime.Deliveries.GetUnpersistedCounters());
        }
        await using var restarted = RuntimeTesting.CreateRuntime([], data.Path);
        restarted.StartCore();
        await OutputPipelineTests.UntilAsync(() => restarted.Deliveries.GetStatus().Store is not null);
        Assert.Equal(100, restarted.Deliveries.GetPersistentCounters()["connection/1/mqtt/acknowledged"]);
        Assert.Equal(1, restarted.Deliveries.GetPersistentCounters()["connection/1/mqtt/wireOversized"]);
        Assert.DoesNotContain(restarted.Deliveries.GetPersistentCounters().Keys, key => key.Contains("secret", StringComparison.Ordinal));
        await restarted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NeverStartedWriterFinalizesAlreadyAccumulatedCounters()
    {
        using var data = TemporaryDirectory.Create();
        var cache = RuntimeTesting.CreateDeliveryCache(data.Path);
        cache.PendingCounters.Add("input/meter/sim/overruns", 8);
        var writer = new DeliveryWriterUnit(cache, TimeProvider.System, new RecordingEventSink(), NullLogger.Instance);
        try
        {
            await writer.DisposeAsync();
            Assert.Equal(8, cache.GetPersistentCounters()["input/meter/sim/overruns"]);
            Assert.Empty(cache.GetUnpersistedCounters());
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task ProductionSimulatorReporterUsesTheRuntimeCounterAccumulator()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([new SimulatorInputFactory()], data.Path);
        await using var plan = runtime.Prepare(new RuntimePlan(1,
            [new RuntimeUnitPlan(RuntimeUnitId.Create("input.simulator", "meter/sim"), JsonSerializer.SerializeToElement(new { }))]));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetUnpersistedCounters()
            .GetValueOrDefault("input/meter/sim/readsCompleted") > 0);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.Deliveries.GetPersistentCounters()["input/meter/sim/readsCompleted"] > 0);
        Assert.Empty(runtime.Deliveries.GetUnpersistedCounters());
    }

    [Fact]
    public async Task InputCountsFlushWithoutDeliveriesAndSurviveRestart()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var reads = new InputReadStatus("meter", "sim", new RecordingEventSink(), runtime.Deliveries.PendingCounters);
            reads.ReadCompleted(DateTimeOffset.UtcNow, 2);
            reads.ReadFailed(DateTimeOffset.UtcNow, "test");
            reads.ReadFailed(DateTimeOffset.UtcNow, "oversize", InputReadFailureKind.DataError);
            Assert.Equal(1, reads.Snapshot().DataRejected);
            reads.CycleSkipped(DateTimeOffset.UtcNow);
            reads.UpstreamTimeInvalid();
            reads.UpstreamTimeInvalid();
            // 无投递请求；应由空闲写者最长 10 秒的周期落盘，而非查询触发写入。
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetPersistentCounters()
                .GetValueOrDefault("input/meter/sim/overruns") == 1);
            Assert.Empty(runtime.Deliveries.GetUnpersistedCounters());
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }

        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path);
        try
        {
            var stored = reopened.GetPersistentCounters();
            Assert.Equal(1, stored["input/meter/sim/readsCompleted"]);
            Assert.Equal(2, stored["input/meter/sim/failedFields"]);
            Assert.Equal(2, stored["input/meter/sim/readsFailed"]);
            Assert.Equal(1, stored["input/meter/sim/readErrors"]);
            Assert.Equal(1, stored["input/meter/sim/dataRejected"]);
            Assert.Equal(1, stored["input/meter/sim/overruns"]);
            Assert.Equal(2, stored["input/meter/sim/invalidUpstreamTimes"]);
        }
        finally { reopened.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task CounterWriteFailureRollsBackDeliveryAndKeepsIncrementsForRetry()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        var cache = runtime.Deliveries;
        _ = cache.GetPersistentCounters();
        Execute(data.Path, "CREATE TRIGGER fail_counter BEFORE INSERT ON queue_counters BEGIN SELECT RAISE(ABORT, 'test-counter-write'); END");
        cache.PendingCounters.Add("input/meter/sim/overruns", 3);
        runtime.StartCore();
        var token = TestContext.Current.CancellationToken;
        var draft = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1, "{}"u8.ToArray(), "fp", "target");
        var failed = await cache.EnqueueAsync([draft], token);
        Assert.Equal(DeliveryWriteStatus.StorageFailed, failed.Status);
        Assert.Empty(cache.ReadPending(1, 0, 10));
        Assert.Empty(cache.GetPersistentCounters());
        Assert.Equal(3, cache.GetUnpersistedCounters()["input/meter/sim/overruns"]);
        Assert.Equal(1, cache.GetUnpersistedCounters()["storageFailedBatches"]);

        cache.PendingCounters.Add("input/meter/sim/overruns", 4);
        Execute(data.Path, "DROP TRIGGER fail_counter");
        Assert.True((await cache.EnqueueAsync([draft], token)).Committed);
        var stored = cache.GetPersistentCounters();
        Assert.Equal(7, stored["input/meter/sim/overruns"]);
        Assert.Equal(1, stored["storageFailedBatches"]);
        Assert.Equal(1, stored["storageFailedRecordAttempts"]);
        Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Empty(cache.GetUnpersistedCounters());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalStopFlushesCountersOrReportsRemainingIncrementsWhenStorageFails(bool writable)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        if (!writable)
        {
            _ = runtime.Deliveries.GetPersistentCounters();
            Execute(data.Path, "CREATE TRIGGER fail_counter BEFORE INSERT ON queue_counters BEGIN SELECT RAISE(ABORT, 'test-final-counter-write'); END");
        }
        runtime.StartCore();
        runtime.Deliveries.PendingCounters.Add("input/meter/sim/overruns", 8);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        if (writable)
        {
            Assert.Equal(8, runtime.Deliveries.GetPersistentCounters()["input/meter/sim/overruns"]);
            Assert.Empty(runtime.Deliveries.GetUnpersistedCounters());
        }
        else
        {
            Assert.Empty(runtime.Deliveries.GetPersistentCounters());
            Assert.Equal(8, runtime.Deliveries.GetUnpersistedCounters()["input/meter/sim/overruns"]);
            Assert.True(runtime.Deliveries.GetStatus().StorageFault);
        }
    }

    private static void Execute(string directory, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, DeliveryCacheOptions.FileName), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
