using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class MqttInputSessionTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static readonly string[] PlanInputKeys = ["device/one", "device/two"];
    private static readonly string[] InputKeys = ["device/input"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveUsesConnectedGenerationCredentialAfterResolverRotates(bool buffered)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("credential-generation");
        var received = new TaskCompletionSource<MqttCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new MqttInputReceiver([new(topic, 1, (_, credential, _) =>
        {
            received.TrySetResult(credential);
            return Task.FromResult(InputHandoffStatus.Committed);
        })], TimeProvider.System, _ => { }, bufferBudget: buffered ? new InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024) : null);
        var credentials = new RotatingCredentials();
        var original = credentials.Current;
        await using var session = new MqttSession(Settings(broker), NewId(), credentials, TimeProvider.System, new CountingConnector(), input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        credentials.Current = original with { Password = "not-the-connected-password", Version = "v2" };
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port)
            .WithClientId(NewId()).WithCleanSession().WithCredentials(MosquittoBroker.UserName, MosquittoBroker.Password).Build(), deadline.Token);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("{\"value\":42}")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), deadline.Token);
        Assert.Equal(original, await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    private sealed class RotatingCredentials : ICredentialResolver
    {
        internal MqttCredential Current = new(MosquittoBroker.UserName, MosquittoBroker.Password, "account", "v1");
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(Current.Username, Current.AccountMarker, Current.Version);
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => Current;
    }

    [Fact]
    public async Task OneSessionSubscribesAndPublishesWithoutAnotherClientOrCallbackDeadlock()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("shared-input-output");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new MqttInputReceiver([new(topic, 1, (message, _) =>
        {
            received.TrySetResult(message.ConvertPayloadToString());
            return Task.FromResult(InputHandoffStatus.Committed);
        })], TimeProvider.System, reason => { if (reason == "acknowledged") { acknowledged.TrySetResult(); } });
        var connector = new CountingConnector();
        var settings = Settings(broker);
        var credentials = new Credentials();
        await using var session = new MqttSession(settings, NewId(), credentials, TimeProvider.System, connector, input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        Assert.False(input.LastSessionPresent);
        var fingerprint = settings.Fingerprint(MosquittoBroker.UserName, "account");
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(new("{\"v\":42}"u8.ToArray(), topic, 1, fingerprint, "test"), deadline.Token)).Status);
        Assert.Equal("{\"v\":42}", await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, connector.Connections);
    }

    [Theory]
    [InlineData(InputHandoffStatus.StorageFailed)]
    [InlineData(InputHandoffStatus.Buffered)]
    public async Task FailedHandoffDisconnectsWithoutAckAndBrokerRedeliversInPersistentSession(InputHandoffStatus firstResult)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("handoff-replay");
        var attempts = 0;
        var replay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<string>();
        var input = new MqttInputReceiver([new(topic, 1, (message, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { return Task.FromResult(firstResult); }
            replay.TrySetResult(message.Dup);
            return Task.FromResult(InputHandoffStatus.Committed);
        })], TimeProvider.System, reason =>
        {
            failures.Enqueue(reason);
            if (reason == "acknowledged")
            { acknowledged.TrySetResult(); }
        });
        var connector = new CountingConnector();
        await using var session = new MqttSession(Settings(broker), NewId(), new Credentials(), TimeProvider.System, connector, input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (publisher)
        { await MqttProbe.PublishAsync(publisher, topic, "{\"v\":42}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token); }
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        Assert.False(acknowledged.Task.IsCompleted);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        Assert.True(input.LastSessionPresent);
        Assert.True(await replay.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(2, attempts);
        Assert.Equal(2, connector.Connections);
        Assert.Contains("handoffFailed", failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffDeadlineDisconnectsAndLateCompletionCannotAcknowledge(bool buffered)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("handoff-deadline");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<InputHandoffStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<string>();
        var budget = new TenonEdge.Runtime.Diagnostics.InFlightBudget(1024 * 1024, 65536);
        var input = new MqttInputReceiver([new(topic, 1, (_, _) =>
        {
            entered.TrySetResult();
            return late.Task; // Deliberately ignores cancellation: must never send a late ACK.
        })], time, reason =>
        {
            reports.Enqueue(reason);
            if (reason == "handoffTimeout")
            { timedOut.TrySetResult(); }
        }, bufferBudget: buffered ? budget : null);
        await using var session = new MqttSession(Settings(broker), NewId(), new Credentials(), time, new CountingConnector(), input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (publisher)
        {
            await MqttProbe.PublishAsync(publisher, topic, "{}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
        }
        await entered.Task.WaitAsync(deadline.Token);
        Assert.DoesNotContain("acknowledged", reports);
        time.Advance(TimeSpan.FromSeconds(5));
        await timedOut.Task.WaitAsync(deadline.Token);
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        try
        {
            if (buffered)
            { Assert.True(budget.Status.ReservedBytes > 0); }
        }
        finally { late.TrySetResult(InputHandoffStatus.Committed); }
        await input.QuiesceAsync(deadline.Token);
        await OutputPipelineTests.UntilAsync(() => budget.Status.ReservedBytes == 0);
        // 连接已先中止；实际迟到绑定结束后才能释放原始额度，仍然不能 ACK。
        Assert.DoesNotContain("acknowledged", reports);
        Assert.False(session.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeitherInputMaintenanceNorOutputCanConnectBeforeActivation(bool send)
    {
        var broker = fixture.Require();
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new CountingConnector();
        var settings = Settings(broker);
        var topic = MqttProbe.NewTopic("activation-gate");
        var input = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { });
        await using var session = new MqttSession(settings, NewId(), new Credentials(), TimeProvider.System, connector, input,
            token => { waiting.TrySetResult(); return activation.Task.WaitAsync(token); });
        using var deadline = MqttProbe.Timeout(Token);
        var pending = send
            ? session.SendAsync(new("{}"u8.ToArray(), topic, 1, settings.Fingerprint(MosquittoBroker.UserName, "account"), "test"), deadline.Token)
            : session.MaintainAsync(deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, connector.Connections);
        Assert.False(session.IsConnected);
        activation.SetResult();
        Assert.Equal(OutputSendStatus.Ack, (await pending).Status);
        Assert.Equal(1, connector.Connections);
    }

    [Fact]
    public async Task TimedOutOutputWaitingForActivationDoesNotConnectOrPoisonSharedSession()
    {
        var broker = fixture.Require();
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var connector = new CountingConnector();
        var settings = Settings(broker);
        var input = new MqttInputReceiver([new(MqttProbe.NewTopic("activation-timeout"), 1,
            (_, _) => Task.FromResult(InputHandoffStatus.Rejected))], time, _ => { });
        var shared = new SharedConnections();
        var clientId = NewId();
        await using var output = new MqttOutputAdapter(shared.Acquire(1, "mqtt", settings.ConnectionIdentity(clientId),
            () => new MqttSession(settings, clientId, new Credentials(), time, connector, input,
                token => { waiting.TrySetResult(); return activation.Task.WaitAsync(token); })), time, 5000);
        var message = new OutputMessage("{}"u8.ToArray(), MqttProbe.NewTopic("output-after-activation"), 1,
            settings.Fingerprint(MosquittoBroker.UserName, "account"), "test");
        var pending = output.SendAsync(message, Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.False(result.CountsTowardPoison);
        Assert.Equal(0, connector.Connections);
        Assert.False(activation.Task.IsCompleted);
        activation.SetResult();
        Assert.Equal(OutputSendStatus.Ack, (await output.SendAsync(message, Token)).Status);
        Assert.Equal(1, connector.Connections);
    }

    [Fact]
    public async Task CompiledInputGroupSharesOneClientAcrossTwoInputBindingsAndTwoOutputBorrowers()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("compiled-group");
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new CountingConnector();
        var shared = new SharedConnections();
        var settings = Settings(broker);
        var clientId = NewId();
        var group = new MqttConnectionGroup(settings, clientId,
            [new(topic + "/one", 1, (message, _) => { first.TrySetResult(message.ConvertPayloadToString()); return Task.FromResult(InputHandoffStatus.Committed); }),
             new(topic + "/two", 1, (message, _) => { second.TrySetResult(message.ConvertPayloadToString()); return Task.FromResult(InputHandoffStatus.Committed); })],
            shared, new Credentials(), TimeProvider.System, connector, token => activation.Task.WaitAsync(token), _ => { });
        await using var inputLease = group.Acquire();
        var outputFactory = new MqttOutputFactory(new Credentials(), TimeProvider.System, connector, shared) { InputGroups = _ => group };
        IOutputTarget BuildOutput(string name) => outputFactory.Build(new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", name),
            JsonSerializer.SerializeToElement(new
            {
                connectionId = settings.ConnectionId,
                host = settings.Host,
                port = settings.Port,
                usePassword = true,
                clientId,
                topic = topic + "/" + name,
                inputs = InputKeys,
            })));
        var targetOne = BuildOutput("one");
        var targetTwo = BuildOutput("two");
        Assert.True(targetOne.CanShareSenderWith(targetTwo));
        await using var outputOne = targetOne.CreateAdapter();
        await using var outputTwo = targetTwo.CreateAdapter();
        Assert.Equal(0, connector.Connections);
        using var deadline = MqttProbe.Timeout(Token);
        activation.SetResult();
        Assert.Equal(OutputSendStatus.Ack, (await inputLease.ExecuteAsync((session, token) => session.MaintainAsync(token), deadline.Token)).Status);
        var fingerprint = settings.Fingerprint(MosquittoBroker.UserName, "account");
        Assert.Equal(OutputSendStatus.Ack, (await outputOne.SendAsync(new("one"u8.ToArray(), topic + "/one", 1, fingerprint, "test"), deadline.Token)).Status);
        Assert.Equal(OutputSendStatus.Ack, (await outputTwo.SendAsync(new("two"u8.ToArray(), topic + "/two", 1, fingerprint, "test"), deadline.Token)).Status);
        Assert.Equal("one", await first.Task.WaitAsync(deadline.Token));
        Assert.Equal("two", await second.Task.WaitAsync(deadline.Token));
        Assert.Equal(1, connector.Connections);
        Assert.Throws<ArgumentException>(() => group.ValidateMember(settings with { Port = broker.Port + 1 }, clientId));
        Assert.Throws<ArgumentException>(() => group.ValidateMember(settings, clientId + "-changed"));
    }

    [Fact]
    public async Task PreparedGroupCannotBorrowOldPlansReceiverEvenWithIdenticalConnectionSettings()
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var clientId = NewId();
        var shared = new SharedConnections();
        var connector = new CountingConnector();
        MqttConnectionGroup Create() => new(settings, clientId,
            [new(MqttProbe.NewTopic("plan-isolation"), 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            shared, new Credentials(), TimeProvider.System, connector, _ => Task.CompletedTask, _ => { });
        var oldPlan = Create();
        var prepared = Create();
        var oldLease = oldPlan.Acquire();
        try
        {
            Assert.Throws<InvalidOperationException>(() => prepared.Acquire());
            Assert.Equal(0, connector.Connections);
        }
        finally { await oldLease.DisposeAsync(); }
        await using var replacement = prepared.Acquire();
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await replacement.ExecuteAsync((session, token) => session.MaintainAsync(token), deadline.Token)).Status);
        Assert.Equal(1, connector.Connections);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task PlanFactoriesRunTwoRealMqttInputsThroughSqliteToSharedMqttOutput(bool restore, bool blockedOtherGroup, bool transition)
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("plan-pipeline");
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = 0;
        var confirmations = 0;
        var connector = new CountingConnector();
        var adapters = new MqttPlanAdapterProvider(new Credentials(), new JsonInputDecoderFactory(), new SharedConnections(), connector,
            TimeProvider.System, Guid.NewGuid().ToString(), (_, reason) =>
            {
                if (reason == "subscribed" && Interlocked.Increment(ref subscriptions) == 2)
                { subscribed.TrySetResult(); }
                if (reason == "acknowledged" && Interlocked.Increment(ref confirmations) == 2)
                { confirmed.TrySetResult(); }
            });
        RuntimeUnitPlan Input(string name) => new(RuntimeUnitId.Create("input.mqtt", "device/" + name),
            JsonSerializer.SerializeToElement(new
            {
                connectionId = 1,
                host = "127.0.0.1",
                port = broker.Port,
                usePassword = true,
                topicFilter = topic + "/" + name,
                fields = new[] { new { key = "value", path = "/v", type = "integer" } },
            }));
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "out"), JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            host = "127.0.0.1",
            port = broker.Port,
            usePassword = true,
            topic = topic + "/out",
            inputs = PlanInputKeys,
        }));
        using var blocked = new RecoveryBlockedInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([blocked], data.Path, planAdapters: [adapters],
            options: new RuntimeOptions { UnitStopTimeout = blockedOtherGroup ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5) });
        RuntimeUnitPlan[] extra = blockedOtherGroup
            ? [new(RuntimeUnitId.Create("input.blocked", "other/input"), JsonSerializer.SerializeToElement(new { }))] : [];
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan([Input("one"), Input("two"), output, .. extra]));
        Assert.Empty(plan.Failures);
        Assert.Equal(0, connector.Connections);
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, topic + "/out", Token);
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            // 再次 Prepare 获得独立工厂；放弃草稿不能改动原订阅或取消原计划。
            await using (var draft = runtime.Prepare(OutputPipelineTests.Plan(Input("one"), Input("two"), output)))
            { Assert.Empty(draft.Failures); }
            Assert.Equal(1, connector.Connections);
            await MqttProbe.PublishAsync(peer, topic + "/one", "{\"v\":9223372036854775807}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token);
            await MqttProbe.PublishAsync(peer, topic + "/two", "{\"v\":42}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token);
            var values = new List<long>();
            for (var i = 0; i < 2; i++)
            {
                var delivered = await received.NextAsync(Token);
                using var payload = JsonDocument.Parse(delivered.ApplicationMessage.ConvertPayloadToString());
                Assert.Equal("device", payload.RootElement.GetProperty("deviceKey").GetString());
                values.Add(payload.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            }
            Assert.Contains(long.MaxValue, values);
            Assert.Contains(42L, values);
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 10).Count == 0);
            Assert.Equal(1, connector.Connections);
            if (restore)
            {
                if (blockedOtherGroup)
                { await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); }
                var quiet = await runtime.QuiesceInputsAsync(Token);
                Assert.Equal(!blockedOtherGroup, quiet.Succeeded);
                if (transition)
                {
                    await using var next = runtime.Prepare(new RuntimePlan(2, [Input("one"), Input("two"),
                        new(RuntimeUnitId.Create("output.mqtt", "renamed"), output.Configuration)]));
                    Assert.Empty(next.Failures);
                    Assert.Equal(1, connector.Connections);
                    await runtime.ActivateCommittedAsync(next, quiet);
                    Assert.Equal(2, runtime.ActiveRevision);
                }
                else
                {
                    var recovery = await runtime.RestoreInputsAsync();
                    Assert.Equal(!blockedOtherGroup, recovery.Succeeded);
                    Assert.True(recovery.Restarted);
                    Assert.Equal(1, recovery.Revision);
                }
                await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref subscriptions) == 4);
                Assert.Equal(2, connector.Connections);
                await MqttProbe.PublishAsync(peer, topic + "/one", "{\"v\":99}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token);
                var delivered = await received.NextAsync(Token);
                using var payload = JsonDocument.Parse(delivered.ApplicationMessage.ConvertPayloadToString());
                Assert.Equal(99, payload.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref confirmations) == 3 && runtime.Deliveries.ReadPending(1, 0, 10).Count == 0);
                Assert.Equal(2, connector.Connections);
            }
            if (blockedOtherGroup)
            {
                Assert.Equal(1, Volatile.Read(ref blocked.Runs)); // MQTT resumed while the unrelated old loop was still alive.
                blocked.Release.TrySetResult();
                await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref blocked.Runs) == 2);
            }
            await runtime.StopAsync(Token);
            Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuiesceDrainsAcceptedMessageButLeavesLaterMessagesForPersistentRedelivery(bool cancelWait)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("quiesce-drain");
        var clientId = NewId();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource<InputHandoffStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var acknowledgements = 0;
        var receiver = new MqttInputReceiver([new(topic, 1, (_, _) =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            return commit.Task;
        })], TimeProvider.System, reason => { if (reason == "acknowledged") { Interlocked.Increment(ref acknowledgements); } });
        var connector = new CountingConnector();
        using var deadline = MqttProbe.Timeout(Token);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (publisher)
        {
            await using (var session = new MqttSession(Settings(broker), clientId, new Credentials(), TimeProvider.System, connector, receiver))
            {
                Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
                await MqttProbe.PublishAsync(publisher, topic, "accepted"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                await entered.Task.WaitAsync(deadline.Token);
                using var waiter = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var drain = receiver.QuiesceAsync(waiter.Token);
                Assert.False(drain.IsCompleted);
                if (cancelWait)
                {
                    await waiter.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
                    drain = receiver.QuiesceAsync(deadline.Token);
                    Assert.False(drain.IsCompleted);
                }
                await MqttProbe.PublishAsync(publisher, topic, "after-quiesce"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                commit.SetResult(InputHandoffStatus.Committed);
                await drain;
                Assert.Equal(1, callbacks);
                Assert.Equal(1, acknowledgements);
                Assert.True(session.IsConnected); // 排空本身不抢先断开，关闭由生命周期拥有者负责。
            }
            var replay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacement = new MqttInputReceiver([new(topic, 1, (message, _) =>
            {
                replay.TrySetResult(message.ConvertPayloadToString());
                return Task.FromResult(InputHandoffStatus.Committed);
            })], TimeProvider.System, _ => { });
            await using var restarted = new MqttSession(Settings(broker), clientId, new Credentials(), TimeProvider.System, connector, replacement);
            Assert.Equal(OutputSendStatus.Ack, (await restarted.MaintainAsync(deadline.Token)).Status);
            Assert.True(replacement.LastSessionPresent);
            Assert.Equal("after-quiesce", await replay.Task.WaitAsync(deadline.Token));
            Assert.Equal(1, callbacks);
            Assert.Equal(1, acknowledgements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeStopKeepsMixedConnectionUntilBlockedSqliteHandoffIsCommittedAndAcknowledged(bool inputsOnly)
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("runtime-drain");
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new CountingConnector();
        var provider = new MqttPlanAdapterProvider(new Credentials(), new JsonInputDecoderFactory(), new SharedConnections(), connector,
            TimeProvider.System, Guid.NewGuid().ToString(), (_, reason) =>
            {
                if (reason == "subscribed")
                { subscribed.TrySetResult(); }
                if (reason == "quiescing")
                { quiescing.TrySetResult(); }
                if (reason == "acknowledged")
                { confirmed.TrySetResult(); }
            });
        var inputId = RuntimeUnitId.Create("input.mqtt", "device/input");
        var input = new RuntimeUnitPlan(inputId, JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            host = "127.0.0.1",
            port = broker.Port,
            usePassword = true,
            topicFilter = topic + "/in",
            fields = new[] { new { key = "value", path = "/v", type = "integer" } },
        }));
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "out"), JsonSerializer.SerializeToElement(new
        { connectionId = 1, host = "127.0.0.1", port = broker.Port, usePassword = true, topic = topic + "/out", inputs = InputKeys }));
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, planAdapters: [provider]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(input, output));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        using var deadline = MqttProbe.Timeout(Token);
        await subscribed.Task.WaitAsync(deadline.Token);
        Task stopped;
        using (var database = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False"))
        {
            database.Open();
            using var transaction = database.BeginTransaction();
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE queue_meta SET config_store_id=config_store_id WHERE id=1";
            command.ExecuteNonQuery();
            var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
            using (publisher)
            { await MqttProbe.PublishAsync(publisher, topic + "/in", "{\"v\":42}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token); }
            await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Single(unit => unit.Id == inputId).Reads?.ReadsCompleted == 1);
            stopped = inputsOnly ? QuiesceAsync() : runtime.StopAsync(Token);
            await quiescing.Task.WaitAsync(deadline.Token);
            Assert.False(stopped.IsCompleted);
            Assert.False(confirmed.Task.IsCompleted);
            transaction.Rollback();
        }
        await stopped.WaitAsync(deadline.Token);
        Assert.True(confirmed.Task.IsCompletedSuccessfully);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10)); // 旧输出已静默，冻结记录留给下次启动。
        Assert.Equal(1, connector.Connections);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.DoesNotContain(runtime.GetStatus().Units, unit => unit.State == RuntimeUnitState.StopTimedOut);
        Assert.Equal(RuntimeUnitState.Stopped, runtime.GetStatus().Units.Single(unit => unit.Id == output.Id).State);
        if (inputsOnly)
        {
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id.Kind == "delivery.writer").State);
            var reconciled = await runtime.Deliveries.ReconcileAsync(new Dictionary<long, string>(), false, Token);
            Assert.True(reconciled.Committed);
        }

        async Task QuiesceAsync()
        {
            var result = await runtime.QuiesceInputsAsync(Token);
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Revision);
        }
    }

    [Fact]
    public async Task ReopenedJournalResubscribesUnchangedRetainedFilterAndUnsubscribesRemovedFilter()
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("subscription-journal");
        using var directory = TemporaryDirectory.Create();
        using var deadline = MqttProbe.Timeout(Token);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (publisher)
        {
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic + "/keep").WithPayload("retained")
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), deadline.Token);
            using (var database = TestConfigStore.Open(directory.Path))
            {
                database.Initialize();
                var journal = new MqttSubscriptionStore(database.Database);
                var retained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiver = new MqttInputReceiver([
                    new(topic + "/keep", 1, (message, _) =>
                    {
                        if (message.Retain) { retained.TrySetResult(); }
                        return Task.FromResult(InputHandoffStatus.Committed);
                    }, true),
                    new(topic + "/remove", 1, (_, _) => Task.FromResult(InputHandoffStatus.Committed))],
                    TimeProvider.System, _ => { }, journal, identity);
                await using var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), receiver);
                Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
                await retained.Task.WaitAsync(deadline.Token);
                Assert.Equal(2, journal.Read(identity).Count);
            }
            using (var database = TestConfigStore.Open(directory.Path))
            {
                database.Initialize();
                var journal = new MqttSubscriptionStore(database.Database);
                var reports = new ConcurrentQueue<string>();
                var received = new ConcurrentQueue<string>();
                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiver = new MqttInputReceiver([
                    new(topic + "/keep", 1, (message, _) =>
                    {
                        received.Enqueue(message.ConvertPayloadToString());
                        if (message.ConvertPayloadToString() == "barrier") { barrier.TrySetResult(); }
                        return Task.FromResult(InputHandoffStatus.Committed);
                    }, true),
                    new(topic + "/added", 0, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
                    TimeProvider.System, reports.Enqueue, journal, identity);
                await using var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), receiver);
                Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
                Assert.True(receiver.LastSessionPresent);
                Assert.Equal(2, reports.Count(reason => reason == "subscribed"));
                Assert.Single(reports, reason => reason == "unsubscribed");
                await MqttProbe.PublishAsync(publisher, topic + "/remove", "removed"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                await MqttProbe.PublishAsync(publisher, topic + "/keep", "barrier"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                await barrier.Task.WaitAsync(deadline.Token);
                Assert.Equal("retained,barrier", string.Join(',', received)); // 显式处理 retained 的绑定接受重订回放。
                Assert.DoesNotContain("unmatched", reports); // 已退订，不再收到删除主题的实时消息。
                Assert.DoesNotContain(journal.Read(identity), item => item.Filter == topic + "/remove");
                Assert.All(journal.Read(identity), item => Assert.True(item.Confirmed));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubackWithFailedLocalCommitLeavesIntentForResubscribeOrRemoval(bool remove)
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("uncertain-suback");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        database.Database.Client.Ado.ExecuteCommand("""
            CREATE TRIGGER fail_subscription_confirmation BEFORE UPDATE ON mqtt_subscription
            WHEN NEW.confirmed = 1 BEGIN SELECT RAISE(ABORT, 'injected confirmation failure'); END
            """);
        using var deadline = MqttProbe.Timeout(Token);
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        {
            Assert.Equal(OutputSendStatus.Retry, (await session.MaintainAsync(deadline.Token)).Status);
            Assert.False(session.IsConnected);
        }
        Assert.Equal(new(topic, 1, false), Assert.Single(journal.Read(identity)));
        database.Database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_subscription_confirmation");
        var reports = new ConcurrentQueue<string>();
        var desired = remove ? topic + "/replacement" : topic;
        var restarted = new MqttInputReceiver([new(desired, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
        await using (var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restarted))
        {
            Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
            Assert.True(restarted.LastSessionPresent);
            Assert.Single(reports, reason => reason == "subscribed");
            Assert.Equal(remove ? 1 : 0, reports.Count(reason => reason == "unsubscribed"));
            Assert.Equal(new(desired, 1, true), Assert.Single(journal.Read(identity)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedJournalResubscribesForQosChangeOrLostBrokerSession(bool sessionLost)
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("subscription-change");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token);
        var original = new MqttInputReceiver([new(topic, 0, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status); }
        if (sessionLost)
        {
            // 测试主动清理 Broker 会话；不是产品旧 ClientId 清理的实现或证据。
            var (cleaner, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, identity.ClientId, cleanSession: true), deadline.Token);
            using (cleaner)
            { await MqttProbe.DisconnectAsync(cleaner, deadline.Token); }
        }
        var qos = sessionLost ? 0 : 1;
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<string>();
        var restarted = new MqttInputReceiver([new(topic, qos, (message, _) =>
        {
            received.TrySetResult((int)message.QualityOfServiceLevel);
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
        await using var current = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restarted);
        Assert.Equal(OutputSendStatus.Ack, (await current.MaintainAsync(deadline.Token)).Status);
        Assert.Equal(!sessionLost, restarted.LastSessionPresent);
        Assert.Single(reports, reason => reason == "subscribed");
        Assert.Equal(sessionLost ? 1 : 0, reports.Count(reason => reason == "sessionLost"));
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (publisher)
        { await MqttProbe.PublishAsync(publisher, topic, "{}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token); }
        Assert.Equal(qos, await received.Task.WaitAsync(deadline.Token));
        Assert.Equal(new(topic, qos, true), Assert.Single(journal.Read(identity)));
    }

    [Fact]
    public async Task UnsubackWithFailedLocalDeleteIsRetriedAfterReconnect()
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("uncertain-unsuback");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token);
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status); }
        database.Database.Client.Ado.ExecuteCommand("""
            CREATE TRIGGER fail_subscription_delete BEFORE DELETE ON mqtt_subscription
            BEGIN SELECT RAISE(ABORT, 'injected deletion failure'); END
            """);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reports = new ConcurrentQueue<string>();
            var receiver = new MqttInputReceiver([new(topic + "/new", 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
                TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
            await using var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), receiver);
            var result = await session.MaintainAsync(deadline.Token);
            if (attempt == 0)
            {
                Assert.Equal(OutputSendStatus.Retry, result.Status);
                Assert.Equal(topic, Assert.Single(journal.Read(identity)).Filter);
                Assert.DoesNotContain("subscribed", reports);
                database.Database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_subscription_delete");
            }
            else
            {
                Assert.Equal(OutputSendStatus.Ack, result.Status);
                Assert.Single(reports, reason => reason == "unsubscribed");
                Assert.Equal(new(topic + "/new", 1, true), Assert.Single(journal.Read(identity)));
            }
        }
    }

    [Fact]
    public async Task FailedIntentCommitNeverInstallsAnUntrackedBrokerSubscription()
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("failed-subscribe-intent");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        database.Database.Client.Ado.ExecuteCommand("""
            CREATE TRIGGER fail_subscription_intent BEFORE INSERT ON mqtt_subscription
            BEGIN SELECT RAISE(ABORT, 'injected intent failure'); END
            """);
        using var deadline = MqttProbe.Timeout(Token);
        var receiver = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), receiver))
        { Assert.Equal(OutputSendStatus.Retry, (await session.MaintainAsync(deadline.Token)).Status); }
        Assert.Empty(journal.Read(identity));
        // 借用同一 Broker 持久会话直接观察：若先发 SUBSCRIBE 再写意图，原主题消息会排在 barrier 前到达。
        var (probe, result) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, identity.ClientId, cleanSession: false), deadline.Token);
        using (probe)
        {
            Assert.True(result.IsSessionPresent);
            var received = ReceivedMessages.Attach(probe);
            await MqttProbe.SubscribeAsync(probe, topic + "/barrier", deadline.Token);
            await MqttProbe.PublishAsync(probe, topic, "unexpected"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            await MqttProbe.PublishAsync(probe, topic + "/barrier", "barrier"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            Assert.Equal(topic + "/barrier", (await received.NextAsync(deadline.Token)).ApplicationMessage.Topic);
        }
    }

    [Fact]
    public async Task OutputOnlyReconnectClearsFormerInputBacklogAndLaterInputRebuildsJournal()
    {
        var broker = fixture.Require();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("last-input-output-only");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token);
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var input = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await input.MaintainAsync(deadline.Token)).Status); }
        Assert.Equal(new(topic, 1, true), Assert.Single(journal.Read(identity)));

        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (publisher)
        {
            var sink = ReceivedMessages.Attach(publisher);
            await MqttProbe.SubscribeAsync(publisher, topic + "/output", deadline.Token);
            // 旧输入已经断开；QoS 1 应积存在 Broker 的旧持久会话中。
            await MqttProbe.PublishAsync(publisher, topic, "stale"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            using (var observer = new MqttClientFactory().CreateMqttClient())
            {
                var backlog = ReceivedMessages.Attach(observer, autoAcknowledge: false);
                var connected = await observer.ConnectAsync(MqttProbe.Options(broker, identity.ClientId, cleanSession: false), deadline.Token);
                Assert.True(connected.IsSessionPresent);
                Assert.Equal("stale", (await backlog.NextAsync(deadline.Token)).ApplicationMessage.ConvertPayloadToString());
                // 不确认，让积压保持在 Broker；排除“根本没有旧消息”的假阳性。
                await MqttProbe.DisconnectAsync(observer, deadline.Token);
            }
            await using (var output = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector()))
            {
                Assert.Equal(OutputSendStatus.Ack, (await output.MaintainAsync(deadline.Token)).Status);
                Assert.Equal(OutputSendStatus.Ack, (await output.SendAsync(new("output"u8.ToArray(), topic + "/output", 1,
                    settings.Fingerprint(MosquittoBroker.UserName, "account"), "test"), deadline.Token)).Status);
                Assert.Equal("output", (await sink.NextAsync(deadline.Token)).ApplicationMessage.ConvertPayloadToString());
            }
            // 产品输出会话的 CleanSession 清理 Broker，但当前不会主动删本地订阅日志；后续输入须按 SessionPresent 重建。
            Assert.Equal(new(topic, 1, true), Assert.Single(journal.Read(identity)));
            var values = new ConcurrentQueue<string>();
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = new ConcurrentQueue<string>();
            var restored = new MqttInputReceiver([new(topic, 1, (message, _) =>
            {
                var value = message.ConvertPayloadToString();
                values.Enqueue(value);
                if (value == "fresh") { fresh.TrySetResult(); }
                return Task.FromResult(InputHandoffStatus.Rejected);
            })], TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
            await using var current = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restored);
            Assert.Equal(OutputSendStatus.Ack, (await current.MaintainAsync(deadline.Token)).Status);
            Assert.False(restored.LastSessionPresent);
            Assert.Single(reports, reason => reason == "sessionLost");
            Assert.Single(reports, reason => reason == "subscribed");
            await MqttProbe.PublishAsync(publisher, topic, "fresh"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            await fresh.Task.WaitAsync(deadline.Token);
            Assert.Equal("fresh", Assert.Single(values));
            Assert.Equal(new(topic, 1, true), Assert.Single(journal.Read(identity)));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task BrokerRestartRestoresOnlySessionsAndBacklogThatWereCheckpointed(bool persistence, bool forceKill, bool checkpoint)
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token, persistence, stablePorts: true);
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("broker-restart");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token, TimeSpan.FromSeconds(60));
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var input = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await input.MaintainAsync(deadline.Token)).Status); }
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (publisher)
        {
            await MqttProbe.PublishAsync(publisher, topic, "offline"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            await MqttProbe.DisconnectAsync(publisher, deadline.Token);
        }
        if (checkpoint)
        {
            await broker.SaveFirstCheckpointAsync(deadline.Token);
            // 该消息已获 PUBACK，但不属于前一个快照；正常停止会保存它，SIGKILL 不会。
            var (later, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
            using (later)
            {
                await MqttProbe.PublishAsync(later, topic, "after-checkpoint"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                await MqttProbe.DisconnectAsync(later, deadline.Token);
            }
        }
        await broker.RestartAsync(forceKill, deadline.Token);
        var reports = new ConcurrentQueue<string>();
        var messages = new ConcurrentQueue<string>();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new MqttInputReceiver([new(topic, 1, (message, _) =>
        {
            var value = message.ConvertPayloadToString();
            messages.Enqueue(value);
            if (value == "barrier") { barrier.TrySetResult(); }
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
        await using var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restored);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var persisted = persistence && (!forceKill || checkpoint);
        Assert.Equal(persisted, restored.LastSessionPresent);
        Assert.Equal(persisted ? 0 : 1, reports.Count(reason => reason == "sessionLost"));
        Assert.Single(reports, reason => reason == "subscribed");
        var (after, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (after)
        { await MqttProbe.PublishAsync(after, topic, "barrier"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token); }
        await barrier.Task.WaitAsync(deadline.Token);
        var expected = !persisted ? "barrier" : checkpoint && !forceKill ? "offline,after-checkpoint,barrier" : "offline,barrier";
        Assert.Equal(expected, string.Join(',', messages));
        Assert.Equal(new(topic, 1, true), Assert.Single(journal.Read(identity)));
    }

    [Fact]
    public async Task ReconnectRestoresSubscriptionMissingFromBrokerCheckpointDespiteSessionPresent()
    {
        // Broker 回滚保留会话但丢失较晚订阅；重连必须重新订阅，不能信任本地 SUBACK 日志。
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token, persistence: true, stablePorts: true);
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("subscription-checkpoint-gap");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token, TimeSpan.FromSeconds(60));
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var first = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await first.MaintainAsync(deadline.Token)).Status); }
        await broker.SaveFirstCheckpointAsync(deadline.Token);
        var positive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new MqttInputReceiver([
            new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected)),
            new(topic + "/later", 1, (_, _) => { positive.TrySetResult(); return Task.FromResult(InputHandoffStatus.Rejected); })],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var second = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), changed))
        {
            Assert.Equal(OutputSendStatus.Ack, (await second.MaintainAsync(deadline.Token)).Status);
            Assert.True(changed.LastSessionPresent);
            var (before, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
            using (before)
            { await MqttProbe.PublishAsync(before, topic + "/later", "positive"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token); }
            await positive.Task.WaitAsync(deadline.Token);
        }
        Assert.Equal(2, journal.Read(identity).Count);
        Assert.All(journal.Read(identity), subscription => Assert.True(subscription.Confirmed));
        await broker.RestartAsync(forceKill: true, deadline.Token);
        var values = new ConcurrentQueue<string>();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<string>();
        Task<InputHandoffStatus> Receive(MqttApplicationMessage message, CancellationToken _)
        {
            var value = message.ConvertPayloadToString();
            values.Enqueue(value);
            if (value == "barrier")
            { barrier.TrySetResult(); }
            return Task.FromResult(InputHandoffStatus.Rejected);
        }
        var restored = new MqttInputReceiver([new(topic, 1, Receive), new(topic + "/later", 1, Receive)],
            TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
        await using var current = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restored);
        Assert.Equal(OutputSendStatus.Ack, (await current.MaintainAsync(deadline.Token)).Status);
        Assert.True(restored.LastSessionPresent);
        Assert.Equal(2, reports.Count(reason => reason == "subscribed"));
        Assert.DoesNotContain("sessionLost", reports);
        var (after, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (after)
        {
            await MqttProbe.PublishAsync(after, topic + "/later", "recovered"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            await MqttProbe.PublishAsync(after, topic, "barrier"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
        }
        await barrier.Task.WaitAsync(deadline.Token);
        Assert.Equal("recovered,barrier", string.Join(',', values));
        Assert.Equal(2, journal.Read(identity).Count); // 本地的确认日志不能证明 Broker 检查点包含同样的订阅。
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task OfflineBrokerQueueDropsNewMessagesBeyondLimitDespitePublisherPubacks(int limit, bool byteLimit)
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token,
            maxQueuedMessages: byteLimit ? 100 : limit, maxQueuedBytes: byteLimit ? limit * 2 - 1 : null);
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var topic = MqttProbe.NewTopic("offline-queue-limit");
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        using var deadline = MqttProbe.Timeout(Token);
        var original = new MqttInputReceiver([new(topic, 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, journal, identity);
        await using (var first = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await first.MaintainAsync(deadline.Token)).Status); }
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (publisher)
        {
            for (var index = 0; index < 10; index++)
            {
                var result = await MqttProbe.PublishAsync(publisher, topic, [(byte)index, 0], MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
                Assert.Equal(MqttClientPublishReasonCode.Success, result.ReasonCode);
            }
            var values = new ConcurrentQueue<byte>();
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = new ConcurrentQueue<string>();
            var restored = new MqttInputReceiver([new(topic, 1, (message, _) =>
            {
                var value = message.Payload.ToArray()[0];
                values.Enqueue(value);
                if (value == 255) { barrier.TrySetResult(); }
                if (values.Count == limit) { drained.TrySetResult(); }
                return Task.FromResult(InputHandoffStatus.Rejected);
            })], TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
            await using var session = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restored);
            Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
            Assert.True(restored.LastSessionPresent);
            Assert.Single(reports, reason => reason == "subscribed");
            await drained.Task.WaitAsync(deadline.Token);
            await MqttProbe.PublishAsync(publisher, topic, [255], MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
            await barrier.Task.WaitAsync(deadline.Token);
            // 字节上限先检查已有积存：2 字节消息在上限 1/5 下会接收 1/3 条，允许最后一条跨界。
            // 因此它不是包括消息元数据在内的严格内存上限；这里只实测此版本的离线载荷积存行为。
            Assert.Equal(Enumerable.Range(0, limit).Select(value => (byte)value).Append((byte)255), values);
            Assert.DoesNotContain("sessionLost", reports);
            Assert.Contains($"Outgoing messages are being dropped for client {identity.ClientId}", await broker.LogsAsync(deadline.Token), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DeviceAccountsCannotSpoofEachOtherAndGatewayAccountIsReadOnly()
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token, deviceAcl: true);
        using var deadline = MqttProbe.Timeout(Token);
        var values = new ConcurrentQueue<string>();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new MqttInputReceiver([new("devices/#", 1, (message, _) =>
        {
            var value = message.ConvertPayloadToString();
            values.Enqueue(message.Topic + ":" + value);
            if (value == "barrier") { barrier.TrySetResult(); }
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, _ => { });
        var settings = Settings(broker);
        await using var gateway = new MqttSession(settings, NewId(), new Credentials(), TimeProvider.System, new CountingConnector(), input);
        Assert.Equal(OutputSendStatus.Ack, (await gateway.MaintainAsync(deadline.Token)).Status);
        using var sensorA = new MqttClientFactory().CreateMqttClient();
        using var sensorB = new MqttClientFactory().CreateMqttClient();
        MqttClientOptions Options(string username) => new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", broker.Port).WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithClientId(NewId()).WithCleanSession().WithCredentials(username, MosquittoBroker.Password).Build();
        Assert.Equal(MQTTnet.MqttClientConnectResultCode.Success, (await sensorA.ConnectAsync(Options("sensor-a"), deadline.Token)).ResultCode);
        Assert.Equal(MQTTnet.MqttClientConnectResultCode.Success, (await sensorB.ConnectAsync(Options("sensor-b"), deadline.Token)).ResultCode);
        const string A = "devices/sensor-a/up";
        const string B = "devices/sensor-b/up";
        Assert.Equal(MqttClientPublishReasonCode.Success, (await MqttProbe.PublishAsync(sensorA, A, "a"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token)).ReasonCode);
        Assert.Equal(MqttClientPublishReasonCode.Success, (await MqttProbe.PublishAsync(sensorA, B, "spoof-b"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token)).ReasonCode);
        Assert.Equal(MqttClientPublishReasonCode.Success, (await MqttProbe.PublishAsync(sensorB, A, "spoof-a"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token)).ReasonCode);
        Assert.Equal(MqttClientPublishReasonCode.Success, (await MqttProbe.PublishAsync(sensorB, B, "b"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token)).ReasonCode);
        // 3.1.1 的 PUBACK 没有“未授权”原因码；发送端的 Ack 不能作为 ACL 放行或终端收到的证据。
        Assert.Equal(OutputSendStatus.Ack, (await gateway.SendAsync(new("gateway-write"u8.ToArray(), A, 1,
            settings.Fingerprint(MosquittoBroker.UserName, "account"), "test"), deadline.Token)).Status);
        await MqttProbe.PublishAsync(sensorA, A, "barrier"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
        await barrier.Task.WaitAsync(deadline.Token);
        Assert.Equal($"{A}:a,{B}:b,{A}:barrier", string.Join(',', values));
    }

    [Fact]
    public async Task ReloadedReadAclAppliesToExistingSubscriptionWithoutReconnecting()
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token, deviceAcl: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(Token, deadline.Token);
        var messages = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var reports = new ConcurrentQueue<string>();
        var receiver = new MqttInputReceiver([new("devices/#", 1, (message, _) =>
        {
            Assert.True(messages.Writer.TryWrite(message.ConvertPayloadToString()));
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reports.Enqueue);
        var connector = new CountingConnector();
        await using var gateway = new MqttSession(Settings(broker), NewId(), new Credentials(), TimeProvider.System, connector, receiver);
        Assert.Equal(OutputSendStatus.Ack, (await gateway.MaintainAsync(operation.Token)).Status);
        using var sensor = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311).WithClientId(NewId()).WithCleanSession()
            .WithCredentials("sensor-a", MosquittoBroker.Password).Build();
        Assert.Equal(MqttClientConnectResultCode.Success, (await sensor.ConnectAsync(options, operation.Token)).ResultCode);

        async Task<string[]> PublishPhaseAsync(string phase)
        {
            foreach (var (topic, payload) in new[]
            {
                ("devices/sensor-a/private", phase + "-private"),
                ("devices/sensor-a/barrier", phase + "-barrier"),
            })
            {
                Assert.Equal(MqttClientPublishReasonCode.Success,
                    (await MqttProbe.PublishAsync(sensor, topic, System.Text.Encoding.UTF8.GetBytes(payload),
                        MqttQualityOfServiceLevel.AtLeastOnce, operation.Token)).ReasonCode);
            }
            var received = new List<string>();
            while (true)
            {
                var payload = await messages.Reader.ReadAsync(operation.Token);
                received.Add(payload);
                if (payload == phase + "-barrier")
                { return received.ToArray(); }
            }
        }

        Assert.Equal(["before-private", "before-barrier"], await PublishPhaseAsync("before"));
        await broker.ReloadDeviceAclAsync(denyPrivateTopic: true, operation.Token);
        // 同一发布连接上先发被禁主题，再发仍允许的屏障；不以“睡眠后没有报文”判定拒绝。
        Assert.Equal(["denied-barrier"], await PublishPhaseAsync("denied"));
        await broker.ReloadDeviceAclAsync(denyPrivateTopic: false, operation.Token);
        Assert.Equal(["restored-private", "restored-barrier"], await PublishPhaseAsync("restored"));
        Assert.True(gateway.IsConnected);
        Assert.True(sensor.IsConnected);
        Assert.Equal(1, connector.Connections);
        Assert.Single(reports, reason => reason == "subscribed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineQueuedMessagesAreRecheckedAgainstReloadedReadAcl(bool revoke)
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        await using var broker = await MosquittoBroker.StartAsync(Token, deviceAcl: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(Token, deadline.Token);
        using var database = TestConfigStore.Create();
        database.Initialize();
        var settings = Settings(broker);
        var identity = new MqttSessionIdentity(settings.Recipient, NewId());
        var original = new MqttInputReceiver([new("devices/#", 1, (_, _) => Task.FromResult(InputHandoffStatus.Rejected))],
            TimeProvider.System, _ => { }, new MqttSubscriptionStore(database.Database), identity);
        await using (var first = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), original))
        { Assert.Equal(OutputSendStatus.Ack, (await first.MaintainAsync(operation.Token)).Status); }

        using var sensor = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311).WithClientId(NewId()).WithCleanSession()
            .WithCredentials("sensor-a", MosquittoBroker.Password).Build();
        Assert.Equal(MqttClientConnectResultCode.Success, (await sensor.ConnectAsync(options, operation.Token)).ResultCode);
        async Task PublishAsync(string topic, string value)
        {
            Assert.Equal(MqttClientPublishReasonCode.Success,
                (await MqttProbe.PublishAsync(sensor, topic, System.Text.Encoding.UTF8.GetBytes(value),
                    MqttQualityOfServiceLevel.AtLeastOnce, operation.Token)).ReasonCode);
        }
        // 原持久客户端已经退出，两个 QoS 1 报文都在旧 ACL 仍允许时到达 Broker。
        await PublishAsync("devices/sensor-a/private", "queued-private");
        await PublishAsync("devices/sensor-a/barrier", "queued-barrier");
        await broker.ReloadDeviceAclAsync(denyPrivateTopic: revoke, operation.Token);

        var messages = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var reports = new ConcurrentQueue<string>();
        var restored = new MqttInputReceiver([new("devices/#", 1, (message, _) =>
        {
            Assert.True(messages.Writer.TryWrite(message.ConvertPayloadToString()));
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reports.Enqueue, new MqttSubscriptionStore(database.Database), identity);
        await using var gateway = new MqttSession(settings, identity.ClientId, new Credentials(), TimeProvider.System, new CountingConnector(), restored);
        Assert.Equal(OutputSendStatus.Ack, (await gateway.MaintainAsync(operation.Token)).Status);
        Assert.True(restored.LastSessionPresent);
        Assert.Single(reports, reason => reason == "subscribed");
        Assert.DoesNotContain("sessionLost", reports);
        var received = new List<string>();
        while (true)
        {
            var value = await messages.Reader.ReadAsync(operation.Token);
            received.Add(value);
            if (value == "queued-barrier")
            { break; }
        }
        Assert.Equal(revoke ? "queued-barrier" : "queued-private,queued-barrier", string.Join(',', received));
        // 再恢复读取权限并发新报文：被撤权丢弃的积存不能随后当作新报文复活。
        await broker.ReloadDeviceAclAsync(denyPrivateTopic: false, operation.Token);
        await PublishAsync("devices/sensor-a/private", "fresh-private");
        await PublishAsync("devices/sensor-a/barrier", "fresh-barrier");
        Assert.Equal("fresh-private", await messages.Reader.ReadAsync(operation.Token));
        Assert.Equal("fresh-barrier", await messages.Reader.ReadAsync(operation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BrokerPayloadBoundaryIsCountedWithoutPoisoningBufferedConnection(int qos)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("payload-boundary");
        var sizes = new ConcurrentQueue<long>();
        var reports = new ConcurrentQueue<string>();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new TenonEdge.Runtime.Diagnostics.InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024);
        var receiver = new MqttInputReceiver([new(topic, qos, (message, _) =>
        {
            sizes.Enqueue(message.Payload.Length);
            if (message.Payload.Length == 1) { barrier.TrySetResult(); }
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reports.Enqueue, bufferBudget: budget);
        var connector = new CountingConnector();
        await using var session = new MqttSession(Settings(broker), NewId(), new Credentials(), TimeProvider.System, connector, receiver);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), deadline.Token);
        using (publisher)
        {
            await MqttProbe.PublishAsync(publisher, topic, new byte[MqttInputReceiver.MaximumPayloadBytes], (MqttQualityOfServiceLevel)qos, deadline.Token);
            await MqttProbe.PublishAsync(publisher, topic, new byte[MqttInputReceiver.MaximumPayloadBytes + 1], (MqttQualityOfServiceLevel)qos, deadline.Token);
            await MqttProbe.PublishAsync(publisher, topic, [1], (MqttQualityOfServiceLevel)qos, deadline.Token);
        }
        await barrier.Task.WaitAsync(deadline.Token);
        await OutputPipelineTests.UntilAsync(() => budget.Status.ReservedBytes == 0 &&
            (qos == 0 || reports.Count(reason => reason == "acknowledged") == 3));
        Assert.Equal($"{MqttInputReceiver.MaximumPayloadBytes},1", string.Join(',', sizes));
        Assert.Single(reports, reason => reason == "oversized");
        Assert.Equal(qos == 1 ? 3 : 0, reports.Count(reason => reason == "acknowledged"));
        Assert.DoesNotContain("wireOversized", reports);
        Assert.DoesNotContain("handoffFailed", reports);
        Assert.DoesNotContain("bufferDropped", reports);
        Assert.DoesNotContain("bufferFull", reports);
        Assert.True(session.IsConnected);
        Assert.Equal(1, connector.Connections);
        Assert.InRange(budget.Status.PeakReservedBytes, 3L * MqttInputReceiver.MaximumPayloadBytes, budget.BufferCapacity);
    }

    private static MqttConnectionSettings Settings(MosquittoBroker broker) => new()
    { ConnectionId = 1, Host = "127.0.0.1", Port = broker.Port, UsePassword = true };
    private static string NewId() => "input-session-" + Guid.NewGuid().ToString("N");
    private sealed class Credentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(MosquittoBroker.UserName, "account", "v1");
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(MosquittoBroker.UserName, MosquittoBroker.Password, "account", "v1");
    }
    private sealed class CountingConnector : IOutboundConnector
    {
        public int Connections { get; private set; }
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stream = await new OutboundConnector(new Loopback()).ConnectAsync(host, port, cancellationToken);
            Connections++;
            return stream;
        }
    }
    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
