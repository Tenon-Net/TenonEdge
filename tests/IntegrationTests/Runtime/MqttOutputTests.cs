using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class MqttOutputTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CommittedSimulatorInputReachesAnIndependentSubscriberAndIsDeletedAfterConfirmation(int qos)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("production-pipeline");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        using (var directory = TemporaryDirectory.Create())
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var credentials = new Credentials();
            await using var runtime = RuntimeTesting.CreateRuntime([new SimulatorInputFactory()], directory.Path,
                outputs: [new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections())]);
            await using var plan = runtime.Prepare(OutputPipelineTests.Plan(Simulator(), Plan(broker.Port, topic, qos)));
            Assert.Empty(plan.Failures);
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            var wire = await received.NextAsync(Token);
            var payload = wire.ApplicationMessage.Payload.ToArray();
            using var json = JsonDocument.Parse(payload);
            Assert.Equal("meter", json.RootElement.GetProperty("deviceKey").GetString());
            Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(topic, wire.ApplicationMessage.Topic);
            Assert.False(wire.ApplicationMessage.Retain);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords >= 1);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.True(runtime.Deliveries.GetStatus().CommittedRecords >= 1);
        }
    }

    [Fact]
    public async Task BrokerNetworkOutageBoundsTheQueueAndRecoveryDeliversFrozenBacklog()
    {
        var broker = fixture.Require();
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        using var directory = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("bounded-outage");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            await using var runtime = RuntimeTesting.CreateRuntime([new SimulatorInputFactory()], directory.Path,
                configureCache: options => new DeliveryCacheOptions
                {
                    DatabasePath = options.DatabasePath,
                    ConfigStoreId = options.ConfigStoreId,
                    CommitInterval = options.CommitInterval,
                    MaximumPendingPerTarget = 3,
                }, outputs: [new MqttOutputFactory(new Credentials(), TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections())]);
            await using var plan = runtime.Prepare(OutputPipelineTests.Plan(Simulator(), Plan(proxy.Port, topic)));
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            await received.NextAsync(Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords >= 1);
            proxy.Freeze();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetQueueTotals(1).QuotaRejected >= 2);
            var backlog = runtime.Deliveries.ReadPending(1, 0, 10);
            Assert.Equal(3, backlog.Count);
            Assert.All(backlog, record => Assert.Equal(0, record.FailureCount));
            Assert.Empty(runtime.Deliveries.ReadFailed(1, 0, 10));
            Assert.InRange(runtime.InFlight.Status.ReservedBytes, 0, 64L * 1024 * 1024);
            var remaining = backlog.ToDictionary(record => record.DeliveryId.ToString("D"), record => record.Payload.ToArray());
            proxy.Thaw();
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(Token);
            recovery.CancelAfter(TimeSpan.FromSeconds(30));
            while (remaining.Count > 0)
            {
                var message = await received.NextAsync(recovery.Token, TimeSpan.FromSeconds(30));
                var bytes = message.ApplicationMessage.Payload.ToArray();
                using var json = JsonDocument.Parse(bytes);
                if (remaining.Remove(json.RootElement.GetProperty("messageId").GetString()!, out var frozen))
                {
                    Assert.Equal(frozen, bytes); // 同一 deliveryId、原采集时间与值，不重生成冻结报文。
                }
                Assert.InRange(runtime.Deliveries.GetQueueTotals(1).PendingCount, 0, 3);
            }
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetQueueTotals(1).PendingCount == 0);
            Assert.True(runtime.Deliveries.GetPersistentCounters()["target/1/quotaRejectedRecords"] >= 2);
            await runtime.StopAsync(Token);
            Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        }
    }

    [Fact]
    public async Task AccessDeniedByTheRealConnectorSuspendsWithoutOpeningATcpConnection()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var target = new MqttOutputFactory(new Credentials(), TimeProvider.System,
            new TenonEdge.Runtime.Connections.OutboundConnector(new DenyPolicy()), new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(port, "denied"));
        await using var adapter = target.CreateAdapter();
        var result = await adapter.SendAsync(target.CreateMessage(Message(), Guid.NewGuid()), Token);
        Assert.Equal(OutputSendStatus.SuspendTarget, result.Status);
        Assert.False(result.CountsTowardPoison);
        Assert.Contains("出站", result.Error, StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }

    private sealed class DenyPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => false;
    }

    [Fact]
    public async Task TwoBorrowersShareOneRealMqttClientUntilTheLastRelease()
    {
        var broker = fixture.Require();
        var connector = new CountingConnector();
        var manager = new TenonEdge.Runtime.Connections.SharedConnections();
        var target = new MqttOutputFactory(new Credentials(), TimeProvider.System, connector, manager)
            .Build(Plan(broker.Port, MqttProbe.NewTopic("shared-session")));
        await using var first = target.CreateAdapter();
        await using var second = target.CreateAdapter();
        var message = target.CreateMessage(Message(), Guid.NewGuid());
        Assert.Equal(OutputSendStatus.Ack, (await first.SendAsync(message, Token)).Status);
        Assert.Equal(OutputSendStatus.Ack, (await second.SendAsync(message, Token)).Status);
        Assert.Equal(1, connector.Connections);
        await first.DisposeAsync();
        Assert.Equal(OutputSendStatus.Ack, (await second.SendAsync(message, Token)).Status);
        Assert.Equal(1, connector.Connections);
        await second.DisposeAsync();
        await using var replacement = target.CreateAdapter();
        Assert.Equal(OutputSendStatus.Ack, (await replacement.SendAsync(message, Token)).Status);
        Assert.Equal(2, connector.Connections);
    }

    private sealed class CountingConnector : IOutboundConnector
    {
        public int Connections { get; private set; }
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stream = await LoopbackConnector.ConnectAsync(host, port, cancellationToken);
            Connections++;
            return stream;
        }
    }

    [Fact]
    public async Task BadAuthenticationSuspendsAndCredentialRotationResumesTheSamePendingRecord()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("credential-rotation");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        using (var directory = TemporaryDirectory.Create())
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var credentials = new Credentials { Value = new(MosquittoBroker.UserName, "wrong-password", "same-account", "v1") };
            var events = new RecordingEventSink();
            await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, events,
                outputs: [new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections())]);
            await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Plan(broker.Port, topic)));
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            await OutputPipelineTests.UntilAsync(() => events.OfType("output.suspended").Count == 1);
            var pending = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(0, pending.FailureCount);
            Assert.Equal(0, runtime.Deliveries.GetStatus().DeletedRecords);
            credentials.Value = credentials.Value with { Password = MosquittoBroker.Password, Version = "v2" };
            var wire = await received.NextAsync(Token);
            Assert.Equal(pending.Payload.ToArray(), wire.ApplicationMessage.Payload.ToArray());
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.Single(events.OfType("output.resuming"));
            Assert.DoesNotContain("wrong-password", JsonSerializer.Serialize(events.Events), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChangedAccountFingerprintDoesNotPublishAnOldRecord()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("changed-account");
        var credentials = new Credentials();
        var target = new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(broker.Port, topic));
        var message = target.CreateMessage(Message(), Guid.NewGuid());
        credentials.Value = credentials.Value with { AccountMarker = "changed-account", Version = "v2" };
        await using var adapter = target.CreateAdapter();
        Assert.Equal(OutputSendStatus.SuspendTarget, (await adapter.SendAsync(message, Token)).Status);
    }

    [Fact]
    public async Task MissingPubackTimesOutAndTheCommittedRecordSurvivesShutdownAndRestart()
    {
        var broker = fixture.Require();
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        using var directory = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("missing-puback");
        var credentials = new Credentials();
        var target = new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(proxy.Port, topic));
        var events = new RecordingEventSink();
        StoredDelivery pending;
        await using (var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, events,
            outputs: [new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections())]))
        {
            await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Plan(proxy.Port, topic)));
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            proxy.Freeze();
            var deliveryId = Guid.NewGuid();
            var message = Message();
            var output = target.CreateMessage(message, deliveryId);
            Assert.True((await runtime.Deliveries.EnqueueAsync([new DeliveryDraft(deliveryId, 1, message.SourceMessageId, message.DeviceKey,
                1, output.Payload, output.Fingerprint, output.FingerprintSummary, output.Address, output.QualityOfService)], Token)).Committed);
            await OutputPipelineTests.UntilAsync(() => events.Events.Any(entry => entry.Description.Contains("超时", StringComparison.Ordinal)));
            pending = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(deliveryId, pending.DeliveryId);
            Assert.Equal(0, pending.FailureCount);
            Assert.Equal(1, runtime.Deliveries.GetStatus().DeletedRecords);
            var stopped = Stopwatch.StartNew();
            await runtime.StopAsync(Token);
            Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(5));
        }

        proxy.Thaw();
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            await using var restarted = RuntimeTesting.CreateRuntime([], directory.Path, outputs: [new MqttOutputFactory(credentials, TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections())]);
            // 有待发记录的输出仍引用种子输入；本次保留输入定义，测试工厂不产生新采样。
            await using var plan = restarted.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Plan(proxy.Port, topic)));
            // 无输入模块只使输入构建失败；已持久化的输出继续处理。
            Assert.Single(plan.Failures);
            restarted.StartCore();
            await restarted.ActivateAsync(plan);
            var wire = await received.NextAsync(Token);
            Assert.Equal(pending.Payload.ToArray(), wire.ApplicationMessage.Payload.ToArray());
            await OutputPipelineTests.UntilAsync(() => restarted.Deliveries.GetStatus().DeletedRecords == 1);
        }
    }

    [Fact]
    public async Task QosZeroHasOnlyLocalCompletionAndExplicitPingDetectsABrokenLinkDuringContinuousPublishing()
    {
        var broker = fixture.Require();
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var target = new MqttOutputFactory(new Credentials(), TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(proxy.Port, MqttProbe.NewTopic("qos0-ping"), qos: 0));
        await using var adapter = target.CreateAdapter();
        Assert.Equal(OutputSendStatus.Ack, (await adapter.SendAsync(target.CreateMessage(Message(), Guid.NewGuid()), Token)).Status);
        proxy.Freeze();
        // 写入本地缓冲仍会成功；这一结果不是 Broker 接收确认。
        Assert.Equal(OutputSendStatus.Ack, (await adapter.SendAsync(target.CreateMessage(Message(), Guid.NewGuid()), Token)).Status);
        await Task.Delay(1100, Token);
        Assert.Equal(OutputSendStatus.Retry, (await adapter.SendAsync(target.CreateMessage(Message(), Guid.NewGuid()), Token)).Status);
    }

    [Fact]
    public async Task UntrustedTlsCertificatesSuspendInsteadOfDisablingValidation()
    {
        var broker = fixture.Require();
        var target = new MqttOutputFactory(new Credentials(), TimeProvider.System, LoopbackConnector, new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(broker.TlsPort, MqttProbe.NewTopic("tls-refusal"), tls: true));
        await using var adapter = target.CreateAdapter();
        var result = await adapter.SendAsync(target.CreateMessage(Message(), Guid.NewGuid()), Token);
        Assert.Equal(OutputSendStatus.SuspendTarget, result.Status);
        Assert.Contains("TLS", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostUsesEncryptedApiCredentialsAndItsEffectiveSeedForRealMqttOutput()
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("production-host");
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = bootstrap.CreateClient();
            using var setup = await client.CompleteSetupAsync(Token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            await client.UseSessionAsync(setup, Token);
            await OutputPipelineTests.UntilAsync(() => bootstrap.Services.GetRequiredService<DeliveryCache>().GetStatus().Store is not null);
            using var credential = await client.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/mqtt-credential", UriKind.Relative),
                new { recipient = $"mqtt://127.0.0.1:{broker.Port}", username = MosquittoBroker.UserName, password = MosquittoBroker.Password }, Token);
            Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        }

        var seed = Path.Combine(seeds.Path, "mqtt-output.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new[] { Simulator(), Plan(broker.Port, topic, useDefaultClientId: true) }.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }),
        }), Token);
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed, ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            using var client = host.CreateClient();
            var wire = await received.NextAsync(Token);
            using var json = JsonDocument.Parse(wire.ApplicationMessage.Payload.ToArray());
            Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            var snapshot = versions.Load(versions.ReadPointers().EffectiveRevision!.Value)!;
            Assert.DoesNotContain(MosquittoBroker.Password, snapshot.Content, StringComparison.Ordinal);
            var runtime = host.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords >= 1);
        }
    }

    private static IOutboundConnector LoopbackConnector => new TenonEdge.Runtime.Connections.OutboundConnector(new LoopbackPolicy());
    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }

    private static RuntimeUnitPlan Simulator() => new(RuntimeUnitId.Create("input.simulator", "meter/sim"),
        JsonSerializer.SerializeToElement(new { periodMs = 1000, sample = new { value = 42 } }));

    private static readonly string[] InputReferences = ["meter/sim"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string NewClientId() => "t1-output-" + Guid.NewGuid().ToString("N");
    private static RuntimeUnitPlan Plan(int port, string topic, int qos = 1, bool tls = false, bool useDefaultClientId = false) => new(RuntimeUnitId.Create("output.mqtt", "broker"),
        JsonSerializer.SerializeToElement(new { connectionId = 1, host = "127.0.0.1", port, tls, usePassword = true, topic, qos, timeoutMs = 700, pingIntervalMs = 1000, clientId = useDefaultClientId ? null : NewClientId(), inputs = InputReferences }));
    private static EdgeMessage Message()
    {
        var at = DateTimeOffset.UtcNow;
        return new EdgeMessage(Guid.NewGuid(), "sim", "meter", at, TimeSource.GatewayUnsynced, at,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(43) });
    }

    private sealed class Credentials : ICredentialResolver
    {
        private MqttCredential _value = new(MosquittoBroker.UserName, MosquittoBroker.Password, "same-account", "v1");
        public MqttCredential Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => Value is { } value ? new(value.Username, value.AccountMarker, value.Version) : null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => Value;
    }
}
