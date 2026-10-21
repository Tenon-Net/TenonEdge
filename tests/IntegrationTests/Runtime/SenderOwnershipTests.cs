using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class SenderOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasReplacementWaitsForLateAckAndDeletesWithoutResending(bool supersede)
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path);
        runtime.StartCore();
        var token = TestContext.Current.CancellationToken;
        var deadline = TimeSpan.FromSeconds(10);
        var events = new RecordingEventSink();
        var supervisor = new UnitSupervisor(new RuntimeOptions(), TimeProvider.System, events, NullLogger.Instance);
        var group = RuntimeUnitId.Create("sender.connection", "1");
        var state = new OutputSenderState();
        var old = new Target("old", blocked: true);
        var obsolete = new Target("obsolete", blocked: false);
        var latest = new Target("latest", blocked: false);
        var builds = 0;
        OutputSenderUnit Build(Target target) => new(target, state, runtime.Deliveries, TimeProvider.System, events,
            new RestartBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100), 0, () => 0));
        Assert.True((await runtime.Deliveries.EnqueueAsync([
            new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1, new byte[] { 42 }, "fingerprint", "summary",
                targetAddress: "frozen-topic", qualityOfService: 1)], token)).Committed);
        Assert.True(supervisor.TryStart(Build(old), () => Build(old), null, group));
        try
        {
            await old.Sending.Task.WaitAsync(deadline, token);
            Assert.Equal(old.Id, Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), token)));
            var candidate = supersede ? obsolete : latest;
            Assert.True(supervisor.TryRestartStopped(candidate.Id, () => { Interlocked.Increment(ref builds); return Build(candidate); }, null, group));
            if (supersede)
            {
                Assert.Empty(await supervisor.StopAsync(id => id == obsolete.Id, deadline, token));
                Assert.True(supervisor.TryRestartStopped(latest.Id, () => { Interlocked.Increment(ref builds); return Build(latest); }, null, group));
            }
            Assert.Equal(0, Volatile.Read(ref builds));
            old.ReleaseSend.TrySetResult(); // Ignores cancellation and ACKs after the stop timeout.
            await old.Disposing.Task.WaitAsync(deadline, token);
            Assert.Equal(1, state.PendingAcknowledgementCount);
            var pending = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(1, pending.Revision);
            Assert.Equal(0, Volatile.Read(ref builds));
            old.ReleaseDispose.TrySetResult();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.Equal(1, Volatile.Read(ref builds));
            Assert.Equal(1, old.Sends);
            Assert.Equal(0, latest.Sends);
            Assert.Equal(0, obsolete.Creates);
            Assert.Equal(1, latest.Creates);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        }
        finally
        {
            old.ReleaseSend.TrySetResult();
            old.ReleaseDispose.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, deadline, token));
        }
    }

    private sealed class Target(string key, bool blocked) : IOutputTarget
    {
        public RuntimeUnitId Id { get; } = RuntimeUnitId.Create("output.test", key);
        public long ConnectionId => 1;
        public string? ClientId => null;
        public string Recipient => "test";
        public string CredentialVersion => "version";
        public string CurrentFingerprint => "fingerprint";
        public IReadOnlyList<string> Inputs => [];
        public int Creates;
        public int Sends;
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new NotSupportedException();
        public IOutputAdapter CreateAdapter() { Interlocked.Increment(ref Creates); return new Adapter(this, blocked); }

        private sealed class Adapter(Target target, bool blocked) : IOutputAdapter
        {
            public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref target.Sends);
                target.Sending.TrySetResult();
                if (blocked)
                { await target.ReleaseSend.Task; }
                return new(OutputSendStatus.Ack);
            }
            public async ValueTask DisposeAsync()
            {
                target.Disposing.TrySetResult();
                if (blocked)
                { await target.ReleaseDispose.Task; }
            }
        }
    }
}
