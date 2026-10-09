namespace TenonEdge.IntegrationTests.Probes.Modbus;

public sealed class ModbusTestSlaveSchedulingTests
{
    [Fact]
    public async Task PeerRespondsWhileSynchronousClientBlocksCallingContext()
    {
        var original = SynchronizationContext.Current;
        var blocked = new BlockedContext();
        ModbusTestSlave? slave = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(blocked);
            slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
            using var client = NModbusProbeClient.Connect(ModbusFraming.Tcp, slave.Endpoint, ModbusProbeOptions.Default);
            Assert.Equal(ModbusTestSlave.RegisterValues(0, 2), client.Read(1, 0, 2));
            Assert.Equal(1, slave.RequestCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
            blocked.Resume.TrySetResult();
            if (slave is not null)
            { await slave.DisposeAsync(); }
        }
    }

    private sealed class BlockedContext : SynchronizationContext
    {
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state) =>
            _ = Resume.Task.ContinueWith(_ => callback(state), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
