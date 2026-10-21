using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>T1-05：结果替身 + 真实 queue.db，非 HTTP 协议验证。</summary>
public sealed class OutputFailureTests
{
    [Fact]
    public async Task SenderOwnsFreshReadScopesAndPauseAfterIdentityStillPreventsSending()
    {
        await using var h = await Harness.CreateAsync(1);
        var scopedTarget = new TestTarget();
        using var targetScope = new TargetReadSession(scopedTarget);
        using var controlScope = new ControlReadSession();
        var opens = 0;
        h.Target.OnOpenSession = () => { opens++; return targetScope; };
        h.Target.Fingerprint = "must-not-read-the-unscoped-target";
        h.Controls = new TestControls
        {
            OnOpenSession = () => controlScope,
            BeforeRead = () => throw new InvalidOperationException("Must use sender control scope"),
        };
        var pause = true;
        scopedTarget.OnReadSendIdentity = () =>
        {
            if (pause)
            { controlScope.State = new(true, "pause-at-identity"); }
            return new("fingerprint", "current");
        };
        h.Start();
        Assert.Equal(1, opens);
        await Until(() => h.State.Diagnostics.State == "manuallyPaused");
        Assert.Empty(scopedTarget.Calls);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
        Assert.Equal(0, targetScope.Disposals);
        Assert.Equal(0, controlScope.Disposals);
        pause = false;
        controlScope.State = new(false, "explicit-resume");
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Single(scopedTarget.Calls);
        Assert.Empty(h.Target.Calls);
        await h.StopAsync();
        Assert.Equal(1, targetScope.Disposals);
        Assert.Equal(1, controlScope.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderReleasesReadScopesWhenOpeningControlOrReadingIdentityThrows(bool openingControl)
    {
        await using var h = await Harness.CreateAsync(1);
        var scopedTarget = new TestTarget { OnReadFingerprint = () => throw new IOException("fixture read failure") };
        h.Target.OnReadFingerprint = () => throw new IOException("fixture unscoped read failure");
        using var targetScope = new TargetReadSession(scopedTarget);
        using var controlScope = new ControlReadSession();
        h.Target.OnOpenSession = () => targetScope;
        var controls = new TestControls { OnOpenSession = () => openingControl ? throw new IOException("fixture open failure") : controlScope };
        await using var sender = new OutputSenderUnit(h.Target, h.State, h.Cache, TimeProvider.System, h.Events,
            new RestartBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), 0, () => 0), controls);
        await Assert.ThrowsAsync<IOException>(() => sender.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, targetScope.Disposals);
        Assert.Equal(openingControl ? 0 : 1, controlScope.Disposals);
        Assert.Empty(scopedTarget.Calls);
    }

    [Fact]
    public async Task SenderUsesFreshSendIdentityAndKeepsItsVersionWhenSuspending()
    {
        await using var h = await Harness.CreateAsync(1);
        var reads = 0;
        h.Target.OnReadSendIdentity = () =>
        {
            reads++;
            h.Target.Version = "captured";
            return new("fingerprint", "captured");
        };
        h.Target.OnSend = _ => new(OutputSendStatus.SuspendTarget, "safe authentication failure");
        h.Start();
        await Until(() => h.Events.OfType("output.suspended").Count == 1);
        Assert.Equal("captured", h.State.SuspendedVersion);
        Assert.Equal(1, reads);
        Assert.Single(h.Target.Calls);
    }

    [Fact]
    public async Task ChangedSendIdentityQuarantinesTheFrozenHeadWithoutSendingIt()
    {
        await using var h = await Harness.CreateAsync(1);
        h.Target.OnReadSendIdentity = () =>
        {
            h.Target.Fingerprint = "changed-at-send-check";
            return new(h.Target.Fingerprint, "new-version");
        };
        h.Start();
        await Until(() => h.Cache.GetQueueTotals(1).PendingCount == 0);
        Assert.Empty(h.Target.Calls);
        Assert.Single(h.Cache.ReadQuarantined(1, 0, 10));
    }

    [Fact]
    public async Task PauseCommittedWhileReadingSendIdentityStillPreventsTheSendUntilExplicitResume()
    {
        await using var h = await Harness.CreateAsync(1);
        var controls = new TestControls();
        h.Controls = controls;
        var pause = true;
        h.Target.OnReadSendIdentity = () =>
        {
            if (pause)
            { controls.State = new(true, "paused-at-identity"); }
            return new("fingerprint", h.Target.Version);
        };
        h.Start();
        await Until(() => h.State.Diagnostics.State == "manuallyPaused");
        Assert.Empty(h.Target.Calls);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
        pause = false;
        controls.State = new(false, "explicit-resume");
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Single(h.Target.Calls);
    }

    [Fact]
    public async Task RejectionMovesOnlyOneRecordAndFreezesItsDataAcrossRestart()
    {
        await using var h = await Harness.CreateAsync(2);
        h.Target.OnSend = value => new(value == 1 ? OutputSendStatus.RejectMessage : OutputSendStatus.Ack, "safe rejection");
        var original = h.Cache.ReadPending(1, 0, 10)[0];
        h.Start();
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        await h.StopAsync();
        var reopened = RuntimeTesting.CreateDeliveryCache(h.Directory.Path);
        var failed = Assert.Single(reopened.ReadFailed(1, 0, 10));
        Assert.Equal(original.DeliveryId, failed.DeliveryId);
        Assert.Equal(original.SourceMessageId, failed.SourceMessageId);
        Assert.Equal(original.Payload.ToArray(), failed.Payload.ToArray());
        Assert.Equal(original.CreatedAt, failed.CreatedAt);
        Assert.Equal(original.TargetAddress, failed.TargetAddress);
        Assert.Equal(original.Fingerprint, failed.Fingerprint);
        Assert.Equal("safe rejection", failed.LastError);
        Assert.NotNull(failed.FailedAt);
        Assert.Equal(0, failed.FailureCount);
        Assert.Equal(new DeliveryFailureTotals(1, 0, 0, 0), reopened.GetFailureTotals(1));
        Assert.Equal(new byte[] { 1, 2 }, h.Target.Calls);
        reopened.Store.ClearReadPool();
    }

    [Fact]
    public async Task TwentyConsecutiveRejectionsSuspendAndCredentialChangeProbesAgain()
    {
        await using var h = await Harness.CreateAsync(21);
        h.Target.OnSend = _ => new(OutputSendStatus.RejectMessage);
        h.Start();
        await Until(() => h.Cache.GetFailureTotals(1).Rejected == 20);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(20, h.Target.Calls.Count);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
        h.Target.OnSend = _ => new(OutputSendStatus.Ack);
        h.Target.Version = "rotated";
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Equal(21, h.Target.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulSendResetsConsecutiveRejectionThreshold()
    {
        await using var h = await Harness.CreateAsync(40);
        h.Target.OnSend = value => new(value == 20 ? OutputSendStatus.Ack : OutputSendStatus.RejectMessage);
        h.Start();
        await Until(() => h.Cache.GetFailureTotals(1).Rejected == 39);
        Assert.Equal(40, h.Target.Calls.Count);
        Assert.Empty(h.Cache.ReadPending(1, 0, 10));
        Assert.Single(h.Events.OfType("output.suspended")); // 19 拒绝 + 成功 + 20 拒绝
    }

    [Fact]
    public async Task SuspendKeepsPendingAndDoesNotCountFailure()
    {
        await using var h = await Harness.CreateAsync(2);
        h.Target.OnSend = _ => new(OutputSendStatus.SuspendTarget, "authentication");
        h.Start();
        await Until(() => h.Cache.ReadPending(1, 0, 10)[0].LastError == "authentication");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(h.Target.Calls);
        Assert.All(h.Cache.ReadPending(1, 0, 10), record => Assert.Equal(0, record.FailureCount));
        Assert.Empty(h.Cache.ReadFailed(1, 0, 10));
        h.Target.OnSend = _ => new(OutputSendStatus.Ack);
        h.Target.Version = "new";
        await Until(() => h.Cache.GetStatus().DeletedRecords == 2);
    }

    [Fact]
    public async Task LaterSuccessThenRepeatedHeadFailureIdentifiesPoison()
    {
        await using var h = await Harness.CreateAsync(3);
        h.Target.OnSend = value => value == 1 ? new(OutputSendStatus.Retry, "response failure", true) : new(OutputSendStatus.Ack);
        h.Start();
        await Until(() => h.Cache.GetStatus().DeletedRecords == 2 && h.Cache.ReadFailed(1, 0, 10).Count == 1);
        Assert.Equal(11, Assert.Single(h.Cache.ReadFailed(1, 0, 10)).FailureCount);
        Assert.Equal(Enumerable.Repeat((byte)1, 10).Concat(new byte[] { 2, 1, 3 }), h.Target.Calls);
        Assert.Equal(new DeliveryFailureTotals(0, 1, 0, 0), h.Cache.GetFailureTotals(1));
    }

    [Fact]
    public async Task ConsecutivePoisonRecordsAreIdentifiedIndividually()
    {
        await using var h = await Harness.CreateAsync(4);
        h.Target.OnSend = value => value <= 2 ? new(OutputSendStatus.Retry, "response failure", true) : new(OutputSendStatus.Ack);
        h.Start();
        await Until(() => h.Cache.GetFailureTotals(1).Poisoned == 2);
        Assert.Equal(new byte[] { 3, 1, 4, 2 }, h.Target.Calls.TakeLast(4));
        Assert.All(h.Cache.ReadFailed(1, 0, 10), record => Assert.Equal(11, record.FailureCount));
    }

    [Fact]
    public async Task OnlyPoisonRecordIsRetriedWithoutDiscardAndFailureCountSurvivesRestart()
    {
        await using var h = await Harness.CreateAsync(1);
        h.Target.OnSend = _ => new(OutputSendStatus.Retry, "unknown response", true);
        h.Start();
        await Until(() => h.Cache.ReadPending(1, 0, 10)[0].FailureCount >= 12);
        await h.StopAsync();
        var reopened = RuntimeTesting.CreateDeliveryCache(h.Directory.Path);
        Assert.True(Assert.Single(reopened.ReadPending(1, 0, 10)).FailureCount >= 12);
        Assert.Empty(reopened.ReadFailed(1, 0, 10));
        reopened.Store.ClearReadPool();
    }

    [Fact]
    public async Task TargetWideFailuresAndRecoveryDoNotDiscardMessages()
    {
        await using var h = await Harness.CreateAsync(3);
        h.Target.OnSend = _ => new(OutputSendStatus.Retry, "server failure", true);
        h.Start();
        await Until(() => h.Cache.ReadPending(1, 0, 10).All(record => record.FailureCount >= 10));
        Assert.Empty(h.Cache.ReadFailed(1, 0, 10));
        h.Target.OnSend = _ => new(OutputSendStatus.Ack);
        await Until(() => h.Cache.GetStatus().DeletedRecords == 3);
        Assert.Equal(0, h.Cache.GetFailureTotals(1).Poisoned);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("408")]
    [InlineData("429")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public async Task TransientFailuresNeverCountTowardPoison(string reason)
    {
        await using var h = await Harness.CreateAsync(2);
        h.Target.OnSend = _ => reason switch
        {
            "network" => throw new IOException("secret"),
            "timeout" => throw new TimeoutException("secret"),
            _ => new(OutputSendStatus.Retry, reason),
        };
        h.Start();
        await Until(() => h.Target.Calls.Count >= 12);
        await h.StopAsync();
        Assert.All(h.Target.Calls, value => Assert.Equal(1, value));
        Assert.All(h.Cache.ReadPending(1, 0, 10), record => Assert.Equal(0, record.FailureCount));
        Assert.Empty(h.Cache.ReadFailed(1, 0, 10));
        Assert.DoesNotContain(h.Events.Events, entry => entry.Description.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownLocalExceptionCountsButItsTextIsNotPersisted()
    {
        await using var h = await Harness.CreateAsync(1);
        h.Target.OnSend = _ => throw new InvalidOperationException("secret password");
        h.Start();
        await Until(() => h.Cache.ReadPending(1, 0, 10)[0].FailureCount >= 2);
        await h.StopAsync();
        Assert.DoesNotContain("secret", Assert.Single(h.Cache.ReadPending(1, 0, 10)).LastError!);
        Assert.DoesNotContain(h.Events.Events, entry => entry.Description.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringSendDoesNotChangeFailureCount()
    {
        await using var h = await Harness.CreateAsync(1);
        h.Target.BlockSend = true;
        h.Start();
        await Until(() => h.Target.Calls.Count == 1);
        await h.StopAsync();
        Assert.Equal(0, Assert.Single(h.Cache.ReadPending(1, 0, 10)).FailureCount);
        Assert.Empty(h.Cache.ReadFailed(1, 0, 10));
    }

    [Fact]
    public async Task StorageFailureRetriesStateCommitWithoutResendingRejectedMessage()
    {
        await using var h = await Harness.CreateAsync(1);
        h.Sql("CREATE TRIGGER fail_state BEFORE UPDATE ON delivery BEGIN SELECT RAISE(ABORT, 'state write failed'); END;");
        h.Target.OnSend = _ => new(OutputSendStatus.RejectMessage);
        h.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SenderTask!);
        Assert.Single(h.Target.Calls);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
        Assert.Equal(0, h.Cache.GetFailureTotals(1).Rejected);
        h.Sql("DROP TRIGGER fail_state");
        h.Start(); // 同一目标状态，重建适配器；与监督者工厂的作用一致。
        await Until(() => h.Cache.GetFailureTotals(1).Rejected == 1);
        Assert.Single(h.Target.Calls);
        Assert.Empty(h.Cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task NetworkFailureAfterSuccessfulProbeDoesNotClassifyHeadAsPoison()
    {
        await using var h = await Harness.CreateAsync(3);
        var headAttempts = 0;
        h.Target.OnSend = value => value != 1 ? new(OutputSendStatus.Ack)
            : ++headAttempts == 11 ? new(OutputSendStatus.Retry, "network")
            : new(OutputSendStatus.Retry, "response failure", true);
        h.Start();
        await Until(() => h.Cache.GetFailureTotals(1).Poisoned == 1);
        Assert.Equal(new byte[] { 2, 1, 3, 1 }, h.Target.Calls.TakeLast(4));
        Assert.Equal(11, Assert.Single(h.Cache.ReadFailed(1, 0, 10)).FailureCount);
    }

    [Fact]
    public async Task ImmediateRetrySkipsOneWaitWithoutSkippingTheNextOrChangingFifo()
    {
        await using var h = await Harness.CreateAsync(2);
        var time = new FakeTimeProvider();
        h.SenderTime = time;
        h.Target.OnSend = _ => new(OutputSendStatus.Retry, "temporary", RetryAfter: TimeSpan.FromSeconds(60));
        h.Start();
        await Until(() => h.State.Diagnostics.BackoffVersion is not null);
        var first = h.State.Diagnostics.BackoffVersion!;
        Assert.True(h.State.RequestImmediateRetry(first));
        await AdvanceShortWait(time, () => h.Target.Calls.Count == 2 && h.State.Diagnostics.BackoffVersion is { } next && next != first);
        Assert.False(h.State.RequestImmediateRetry(first));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Target.Calls.Count);
        Assert.All(h.Target.Calls, value => Assert.Equal(1, value));
        Assert.Equal(2, h.Cache.ReadPending(1, 0, 10).Count);
        await h.StopAsync();
        Assert.Null(h.State.Diagnostics.BackoffVersion);
    }

    [Fact]
    public async Task ManualPauseWinsEvenAfterImmediateRetryWasAcceptedAndSuspendCannotBeBypassed()
    {
        await using var h = await Harness.CreateAsync(1);
        var time = new FakeTimeProvider();
        h.SenderTime = time;
        var controls = new TestControls();
        h.Controls = controls;
        h.Target.OnSend = _ => new(OutputSendStatus.Retry, "temporary", RetryAfter: TimeSpan.FromSeconds(60));
        string? version = null;
        controls.BeforeRead = () =>
        {
            if (version is null && h.State.Diagnostics.BackoffVersion is { } window)
            {
                // 在原循环读暂停状态的边界，先接受信号再提交暂停；不依赖线程恰好已注册定时器。
                version = window;
                Assert.True(h.State.RequestImmediateRetry(window));
                controls.State = new(true, "paused");
            }
        };
        h.Start();
        await AdvanceShortWait(time, () => h.State.Diagnostics.State == "manuallyPaused");
        Assert.NotNull(version);
        Assert.Single(h.Target.Calls);
        Assert.False(h.State.RequestImmediateRetry(version));
        h.Target.OnSend = _ => new(OutputSendStatus.SuspendTarget, "HTTP 401");
        controls.State = new(false, "resume");
        await AdvanceShortWait(time, () => h.State.Diagnostics.State == "automaticallyPaused");
        Assert.False(h.State.RequestImmediateRetry(version));
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Target.Calls.Count);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task RetryAfterWaitsAtTargetAndCancellationInterruptsTheWait()
    {
        await using var h = await Harness.CreateAsync(2);
        var time = new FakeTimeProvider();
        h.SenderTime = time;
        h.Target.OnSend = _ => new(OutputSendStatus.Retry, "temporary", RetryAfter: TimeSpan.FromSeconds(5));
        h.Start();
        await Until(() => h.Cache.ReadPending(1, 0, 10)[0].LastError == "temporary");
        await Task.Delay(30, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(4999));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Single(h.Target.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => h.Target.Calls.Count == 2);
        await h.StopAsync();
        Assert.All(h.Target.Calls, value => Assert.Equal(1, value));
        Assert.All(h.Cache.ReadPending(1, 0, 10), record => Assert.Equal(0, record.FailureCount));
    }

    [Fact]
    public async Task OldIdentityIsQuarantinedAndFreshIdentityContinuesSending()
    {
        await using var h = await Harness.CreateAsync(2);
        var seq = h.Cache.ReadPending(1, 0, 10)[0].Seq;
        await h.Cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), TestContext.Current.CancellationToken);
        h.Target.Fingerprint = "new-account";
        await h.Cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1, new byte[] { 99 },
            "new-account", "new", "topic", 1)], TestContext.Current.CancellationToken);
        h.Start();
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Equal(new byte[] { 99 }, h.Target.Calls);
        Assert.Equal(2, h.Cache.ReadQuarantined(1, 0, 10).Count);
        Assert.Empty(h.Cache.ReadFailed(1, 0, 10));
        h.Target.Fingerprint = "fingerprint";
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 99 }, h.Target.Calls);
    }

    [Fact]
    public async Task AdapterDetectingIdentityChangeDuringSendQuarantinesWithoutSuspendingTarget()
    {
        await using var h = await Harness.CreateAsync(2);
        h.Target.OnSend = value => value == 1 ? new(OutputSendStatus.SuspendTarget, FingerprintMismatch: true) : new(OutputSendStatus.Ack);
        h.Start();
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Single(h.Cache.ReadQuarantined(1, 0, 10));
        Assert.Empty(h.Events.OfType("output.suspended"));
        Assert.Equal(new byte[] { 1, 2 }, h.Target.Calls);
    }

    [Fact]
    public async Task ManualPauseAllowsEnqueueAndCredentialChangesDoNotResumeIt()
    {
        await using var h = await Harness.CreateAsync(1);
        var controls = new TestControls { State = new(true, "paused") };
        h.Controls = controls;
        h.Start();
        h.Target.Version = "rotated";
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(h.Target.Calls);
        Assert.Single(h.Cache.ReadPending(1, 0, 10));
        controls.State = new(false, "resume");
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Single(h.Target.Calls);
    }

    [Fact]
    public async Task ExplicitResumeAlsoClearsAutomaticSuspension()
    {
        await using var h = await Harness.CreateAsync(1);
        var controls = new TestControls();
        h.Controls = controls;
        h.Target.OnSend = _ => new(OutputSendStatus.SuspendTarget);
        h.Start();
        await Until(() => h.Events.OfType("output.suspended").Count == 1);
        h.Target.OnSend = _ => new(OutputSendStatus.Ack);
        controls.State = new(false, "resume");
        await Until(() => h.Cache.GetStatus().DeletedRecords == 1);
        Assert.Equal(2, h.Target.Calls.Count);
    }

    private sealed class TestControls : IOutputControl
    {
        public Func<IOutputControlReadSession?>? OnOpenSession { get; set; }
        public IOutputControlReadSession? OpenSenderReadSession() => OnOpenSession?.Invoke();
        public OutputControlState State { get; set; } = new(false, null);
        public Action? BeforeRead { get; set; }
        public OutputControlState Read(long connectionId)
        {
            BeforeRead?.Invoke();
            return State;
        }
    }

    private sealed class TargetReadSession(IOutputTarget target) : IOutputTargetReadSession
    {
        public IOutputTarget Target => target;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class ControlReadSession : IOutputControlReadSession
    {
        public OutputControlState State { get; set; } = new(false, null);
        public int Disposals { get; private set; }
        public OutputControlState Read(long connectionId) => State;
        public void Dispose() => Disposals++;
    }

    private static Task Until(Func<bool> condition) => OutputPipelineTests.UntilAsync(condition);

    private static async Task AdvanceShortWait(FakeTimeProvider time, Func<bool> condition)
    {
        // 窗口发布与 Task.Delay 注册之间也可接受信号；逐步推进避免测试抢在定时器创建前只推进一次。
        // 总共不超过2.5秒虚拟时间，不能靠自然耗尽60秒退避让测试误通过。
        for (var i = 0; i < 10 && !condition(); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TemporaryDirectory Directory { get; } = TemporaryDirectory.Create();
        public RuntimeHost Runtime { get; }
        public DeliveryCache Cache => Runtime.Deliveries;
        public TestTarget Target { get; } = new();
        public RecordingEventSink Events { get; } = new();
        public Task? SenderTask { get; private set; }
        public TimeProvider SenderTime { get; set; } = TimeProvider.System;
        public IOutputControl? Controls { get; set; }
        private readonly OutputSenderState _state = new();
        public OutputSenderState State => _state;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private OutputSenderUnit? _sender;

        private Harness()
        {
            Runtime = RuntimeTesting.CreateRuntime([], Directory.Path);
            Runtime.StartCore();
        }

        public static async Task<Harness> CreateAsync(int count)
        {
            var h = new Harness();
            var drafts = Enumerable.Range(1, count).Select(value => new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1,
                new byte[] { (byte)value }, "fingerprint", "summary", targetAddress: "topic", qualityOfService: 1)).ToArray();
            Assert.True((await h.Cache.EnqueueAsync(drafts, TestContext.Current.CancellationToken)).Committed);
            return h;
        }

        public void Start()
        {
            _sender = new OutputSenderUnit(Target, _state, Cache, SenderTime, Events,
                new RestartBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), 0, () => 0), Controls);
            SenderTask = _sender.RunAsync(_stop.Token);
        }

        public void Sql(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Directory.Path, "queue.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (SenderTask is not null)
            {
                try
                { await SenderTask; }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }

            if (_sender is not null)
            {
                await _sender.DisposeAsync();
            }

            await Runtime.StopAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Runtime.DisposeAsync();
            _stop.Dispose();
            Directory.Dispose();
        }
    }

    private sealed class TestTarget : IOutputTarget
    {
        public Func<IOutputTargetReadSession?>? OnOpenSession { get; set; }
        public IOutputTargetReadSession? OpenSenderReadSession() => OnOpenSession?.Invoke();
        public Func<string>? OnReadFingerprint { get; set; }
        public Func<OutputSendIdentity>? OnReadSendIdentity { get; set; }
        public OutputSendIdentity ReadSendIdentity() => OnReadSendIdentity?.Invoke() ?? new(CurrentFingerprint, CredentialVersion);
        public Func<byte, OutputSendResult> OnSend { get; set; } = _ => new(OutputSendStatus.Ack);
        public ConcurrentQueue<byte> Calls { get; } = new();
        public string Version { get; set; } = "initial";
        public bool BlockSend { get; set; }
        public RuntimeUnitId Id => RuntimeUnitId.Create("output.test", "result");
        public long ConnectionId => 1;
        public string ClientId => "client";
        public string Recipient => "mqtt://localhost";
        public string Fingerprint { get; set; } = "fingerprint";
        public string CurrentFingerprint => OnReadFingerprint?.Invoke() ?? Fingerprint;
        public string CredentialVersion => Version;
        public IReadOnlyList<string> Inputs => [];
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new NotSupportedException();
        public IOutputAdapter CreateAdapter() => new Adapter(this);

        private sealed class Adapter(TestTarget target) : IOutputAdapter
        {
            public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                var value = message.Payload.Span[0];
                target.Calls.Enqueue(value);
                if (target.BlockSend)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return target.OnSend(value);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
