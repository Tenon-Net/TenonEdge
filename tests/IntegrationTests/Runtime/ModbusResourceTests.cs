using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

[CollectionDefinition("Modbus resources", DisableParallelization = true)]
public sealed class ModbusResourceTestGroup;

[Collection("Modbus resources")]
public sealed class ModbusResourceTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaximumConnectionPlanReadsAndStopsWithoutAccumulatingWorkersOrBudget(bool silent)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "工作线程与资源观测使用 Linux /proc；其他环境不是通过。");
        const int count = 256;
        var shared = new SharedConnections();
        var baseline = WorkerCount();
        Report("baseline", null);
        for (var round = 0; round < 3; round++)
        {
            await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = silent });
            var budget = new InFlightBudget(64L * 1024 * 1024);
            var factory = Assert.Single(new ModbusPlanAdapterProvider(shared, new OutboundConnector(new Loopback()), TimeProvider.System).Create().Inputs);
            var units = new List<IRuntimeUnit>();
            var channels = new List<BudgetedInputChannel>();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Task[] runs = [];
            try
            {
                for (var index = 1; index <= count; index++)
                {
                    var channel = new BudgetedInputChannel(1, budget);
                    var key = $"device{index}";
                    var context = new InputUnitContext(key, "sample", TimeProvider.System,
                        new FixedClockSyncStatus(ClockSyncState.NotSynchronized),
                        new InputReadStatus(key, "sample", new RecordingEventSink()), (InputMessageWriter)channel.Writer);
                    var plan = new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", key + "/sample"), JsonSerializer.SerializeToElement(new
                    {
                        connectionId = index,
                        host = "127.0.0.1",
                        port = slave.Endpoint.Port,
                        unitId = 1,
                        periodMs = 60000,
                        readTimeoutMs = silent ? 30000 : 5000,
                        points = new[] { new { key = "value", functionCode = 3, address = index, type = "uint16" } }
                    }));
                    units.Add(factory.Build(plan, context));
                    channels.Add(channel);
                }
                Assert.Equal(baseline, WorkerCount()); // Build 不启动线程或联网。
                Assert.Equal(0, slave.ConnectionCount);
                runs = units.Select(unit => unit.RunAsync(stopping.Token)).ToArray();
                if (silent)
                {
                    await slave.WaitForRequestsAsync(count, TimeSpan.FromSeconds(10), Token);
                    Assert.All(channels, channel => Assert.False(channel.Reader.TryRead(out _)));
                }
                else
                {
                    var messages = await Task.WhenAll(channels.Select(channel => channel.Reader.ReadAsync(Token).AsTask()))
                        .WaitAsync(TimeSpan.FromSeconds(30), Token);
                    for (var index = 0; index < messages.Length; index++)
                    { Assert.Equal(FieldValue.FromInt64(4097 + index), messages[index].Values["value"]); }
                    Assert.Equal(0, budget.Status.ReservedBytes);
                }
                Assert.Equal(count, slave.ConnectionCount);
                Assert.Equal(count, slave.RequestCount);
                Assert.Equal(baseline + count, WorkerCount());
                Assert.InRange(budget.Status.PeakReservedBytes, 1, budget.Capacity);
                Report($"round-{round + 1}-active-silent-{silent}", budget);
            }
            finally
            {
                var stop = Stopwatch.StartNew();
                var cancellation = stopping.CancelAsync();
                await Task.WhenAll([cancellation, .. runs]).WaitAsync(TimeSpan.FromSeconds(5), Token);
                output.WriteLine($"round-{round + 1}-silent-{silent}: stopMs={stop.ElapsedMilliseconds}");
                foreach (var channel in channels)
                { while (channel.Reader.TryRead(out _)) { } }
                foreach (var unit in units)
                { await unit.DisposeAsync(); }
            }
            await OutputPipelineTests.UntilAsync(() => WorkerCount() == baseline);
            Assert.Equal(0, budget.Status.ReservedBytes);
            Report($"round-{round + 1}-stopped", budget);
        }
        Assert.Equal(baseline, WorkerCount());
        Report("finished", null);
    }

    [Fact]
    public async Task MinuteOfContinuousPollingKeepsConnectionWorkerAndBudgetCountsBounded()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "资源观测依赖 Linux /proc；其他环境不是通过。");
        const int connections = 32;
        const int samples = 61;
        var baseline = WorkerCount();
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var budget = new InFlightBudget(64L * 1024 * 1024);
        var factory = Assert.Single(new ModbusPlanAdapterProvider(new SharedConnections(), new OutboundConnector(new Loopback()), TimeProvider.System).Create().Inputs);
        var units = new List<IRuntimeUnit>();
        var channels = new List<BudgetedInputChannel>();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task[] runs = [];
        Report("soak-before", budget);
        var elapsed = Stopwatch.StartNew();
        try
        {
            for (var index = 1; index <= connections; index++)
            {
                var channel = new BudgetedInputChannel(1, budget);
                var key = $"device{index}";
                var context = new InputUnitContext(key, "sample", TimeProvider.System,
                    new FixedClockSyncStatus(ClockSyncState.NotSynchronized),
                    new InputReadStatus(key, "sample", new RecordingEventSink()), (InputMessageWriter)channel.Writer);
                var plan = new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", key + "/sample"), JsonSerializer.SerializeToElement(new
                {
                    connectionId = index,
                    host = "127.0.0.1",
                    port = slave.Endpoint.Port,
                    unitId = 1,
                    periodMs = 1000,
                    readTimeoutMs = 5000,
                    points = Enumerable.Range(0, 64).Select(address => new { key = $"v{address}", functionCode = 3, address, type = "uint16" }).ToArray()
                }));
                units.Add(factory.Build(plan, context));
                channels.Add(channel);
            }
            runs = units.Select(unit => unit.RunAsync(stopping.Token)).ToArray();
            for (var sample = 0; sample < samples; sample++)
            {
                var messages = await Task.WhenAll(channels.Select(channel => channel.Reader.ReadAsync(Token).AsTask()))
                    .WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.All(messages, message =>
                {
                    Assert.Equal(64, message.Values.Count);
                    Assert.Equal(FieldValue.FromInt64(4096), message.Values["v0"]);
                    Assert.Equal(FieldValue.FromInt64(4159), message.Values["v63"]);
                    Assert.Empty(message.Quality);
                });
                Assert.Equal(connections, messages.Select(message => message.DeviceKey).Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(baseline + connections, WorkerCount());
                Assert.InRange(budget.Status.ReservedBytes, 0, budget.Capacity);
                if (sample % 20 == 0)
                { Report($"soak-sample-{sample + 1}", budget); }
            }
            Assert.Equal(connections, slave.ConnectionCount);
            Assert.Equal(connections * samples, slave.RequestCount);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(59));
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), Token);
            foreach (var channel in channels)
            { while (channel.Reader.TryRead(out _)) { } }
            foreach (var unit in units)
            { await unit.DisposeAsync(); }
        }
        await OutputPipelineTests.UntilAsync(() => WorkerCount() == baseline);
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.InRange(budget.Status.PeakReservedBytes, 1, budget.Capacity);
        Report("soak-stopped", budget);
        output.WriteLine($"soak: elapsedMs={elapsed.ElapsedMilliseconds}, requests={slave.RequestCount}, decodedFields={connections * samples * 64}");
    }

    private void Report(string stage, InFlightBudget? budget)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        output.WriteLine($"{stage}: modbusWorkers={WorkerCount()}, processThreads={process.Threads.Count}, rssBytes={process.WorkingSet64}, fdCount={Directory.GetFiles("/proc/self/fd").Length}, budgetPeak={budget?.Status.PeakReservedBytes ?? 0}, budgetHeld={budget?.Status.ReservedBytes ?? 0}");
    }

    private static int WorkerCount()
    {
        var count = 0;
        foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(task, "comm")).StartsWith("TenonEdge.Modbu", StringComparison.Ordinal))
                { count++; }
            }
            catch (IOException) { } // 无关 CLR 线程可能在枚举期间退出；稳定工作线程仍应存在。
        }
        return count;
    }

    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
