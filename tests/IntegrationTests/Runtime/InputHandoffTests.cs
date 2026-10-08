using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputHandoffTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["meter/sim"];
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };

    [Fact]
    public async Task ReceiptCannotCompleteWhileSqliteWriteLockPreventsCommit()
    {
        using var data = TemporaryDirectory.Create();
        var input = new ConfirmedInput();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [OutputFactory()]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output()));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        using (var connection = Open(data.Path))
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE queue_meta SET config_store_id=config_store_id WHERE id=1";
            command.ExecuteNonQuery();
            input.Allow.TrySetResult();
            await input.Produced.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(input.Result.Task.IsCompleted);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
            transaction.Rollback();
        }
        Assert.Equal(InputHandoffStatus.Committed, await input.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        await runtime.StopAsync(Token);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("no-output")]
    [InlineData("mapping")]
    [InlineData("quota")]
    public async Task ExplicitlyDiscardedSourceReturnsRejectedInsteadOfWaitingForever(string reason)
    {
        using var data = TemporaryDirectory.Create();
        var input = new ConfirmedInput();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [OutputFactory()],
            configureCache: options => new DeliveryCacheOptions
            {
                DatabasePath = options.DatabasePath,
                ConfigStoreId = options.ConfigStoreId,
                CommitInterval = options.CommitInterval,
                MaximumPendingBytesPerTarget = reason == "quota" ? 1 : options.MaximumPendingBytesPerTarget,
            });
        await using var plan = runtime.Prepare(reason == "no-output" ? OutputPipelineTests.Plan(OutputPipelineTests.Input()) :
            OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output(reason == "mapping")));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        input.Allow.TrySetResult();
        Assert.Equal(InputHandoffStatus.Rejected, await input.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        await runtime.StopAsync(Token);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        if (reason != "quota")
        { Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["input/meter/sim/noDeliveryMessages"]); }
    }

    [Fact]
    public async Task CommittedBranchAndExplicitRejectedBranchPermitSourceConfirmation()
    {
        using var data = TemporaryDirectory.Create();
        var input = new ConfirmedInput();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [OutputFactory()]);
        var rejected = Output(reject: true);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output(),
            new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "rejected"), rejected.Configuration)));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        input.Allow.TrySetResult();
        Assert.Equal(InputHandoffStatus.Committed, await input.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        await runtime.StopAsync(Token);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["route/meter/sim/output.http/rejected/requiredFieldFailures"]);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task StorageRollbackReturnsUnacknowledgeableFailureNotPollerDataLoss()
    {
        using var data = TemporaryDirectory.Create();
        var input = new ConfirmedInput();
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, events, outputs: [OutputFactory()]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        using (var connection = Open(data.Path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_handoff BEFORE INSERT ON delivery BEGIN SELECT abs(-9223372036854775808); END";
            command.ExecuteNonQuery();
        }
        input.Allow.TrySetResult();
        Assert.Equal(InputHandoffStatus.StorageFailed, await input.Result.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        await runtime.StopAsync(Token);
        Assert.Single(events.OfType("input.handoffFailed"));
        Assert.Empty(events.OfType("input.storageLoss"));
        Assert.False(runtime.Deliveries.GetPersistentCounters().ContainsKey("storageLostSourceMessages"));
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["input/meter/sim/handoffFailed"]);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    [Trait("Category", "RealBroker")]
    public async Task BrokerRedeliveryIsAcknowledgedOnlyAfterActualSqliteCommit()
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        var input = new BridgedInput();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [OutputFactory()]);
        await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output()));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        var context = await input.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        using var connection = Open(data.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_broker_handoff BEFORE INSERT ON delivery BEGIN SELECT abs(-9223372036854775808); END";
        command.ExecuteNonQuery();
        var topic = MqttProbe.NewTopic("sqlite-handoff");
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var replayed = false;
        var committedBeforeAck = false;
        var handoff = new MqttInputHandoff(context, new JsonInputDecoderFactory().Build(
            JsonSerializer.Deserialize<JsonElement>("""[{"key":"value","path":"/value","type":"integer"}]""")));
        var receiver = new MqttInputReceiver([new(topic, 1, async (message, cancellationToken) =>
        {
            Interlocked.Increment(ref attempts);
            replayed = message.Dup;
            return await handoff.ReceiveAsync(message, cancellationToken);
        })], TimeProvider.System, reason =>
        {
            if (reason == "acknowledged")
            {
                committedBeforeAck = runtime.Deliveries.ReadPending(1, 0, 10).Count == 1;
                acknowledged.TrySetResult();
            }
        });
        await using var session = new MqttSession(new MqttConnectionSettings
        { ConnectionId = 1, Host = "127.0.0.1", Port = broker.Port, UsePassword = true },
            "sqlite-handoff-" + Guid.NewGuid().ToString("N"), new BrokerCredentials(), TimeProvider.System,
            new OutboundConnector(new Loopback()), receiver);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using (publisher)
        {
            await MqttProbe.PublishAsync(publisher, topic, "{\"value\":42}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, deadline.Token);
        }
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        Assert.Equal(1, attempts);
        Assert.False(acknowledged.Task.IsCompleted);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        command.CommandText = "DROP TRIGGER fail_broker_handoff";
        command.ExecuteNonQuery();
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        await acknowledged.Task.WaitAsync(deadline.Token);
        Assert.True(receiver.LastSessionPresent);
        Assert.True(replayed);
        Assert.True(committedBeforeAck);
        Assert.Equal(2, attempts);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        await runtime.StopAsync(Token);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["input/meter/sim/handoffFailed"]);
    }

    private sealed class BridgedInput : IInputUnitFactory
    {
        public string Kind => "input.test";
        public bool UsesDurableHandoff => true;
        public TaskCompletionSource<InputUnitContext> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => new Unit(this, unit.Id, context);
        private sealed class Unit(BridgedInput owner, RuntimeUnitId id, InputUnitContext context) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                owner.Ready.TrySetResult(context);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class BrokerCredentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(MosquittoBroker.UserName, "account", "v1");
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(MosquittoBroker.UserName, MosquittoBroker.Password, "account", "v1");
    }
    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }

    private static SqliteConnection Open(string directory)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        return connection;
    }
    private static HttpOutputFactory OutputFactory() => new(new Anonymous(), TimeProvider.System, new Blocked(), new SharedConnections());
    private static RuntimeUnitPlan Output(bool reject = false) => new(RuntimeUnitId.Create("output.http", "out"),
        JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            baseAddress = "http://localhost/",
            inputs = Inputs,
            mapping = reject ? JsonSerializer.Deserialize<JsonElement>("""[{"target":"must","type":"integer","sourceField":"missing","required":true}]""") : default,
        }, JsonOptions));
    private sealed class Anonymous : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }
    private sealed class Blocked : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new OutboundAccessDeniedException();
    }
    private sealed class ConfirmedInput : IInputUnitFactory
    {
        public string Kind => "input.test";
        public bool UsesDurableHandoff => true;
        public TaskCompletionSource Allow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Produced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InputHandoffStatus> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => new Unit(this, unit.Id, context);
        private sealed class Unit(ConfirmedInput owner, RuntimeUnitId id, InputUnitContext context) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                await owner.Allow.Task.WaitAsync(stoppingToken);
                var result = await context.Messages.ProduceConfirmedAsync(1024, 1024, _ =>
                {
                    var at = context.Time.GetUtcNow();
                    var message = new EdgeMessage(Guid.NewGuid(), context.InputKey, context.DeviceKey, at, TimeSource.GatewayUnsynced, at,
                        new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) });
                    owner.Produced.TrySetResult();
                    return ValueTask.FromResult<EdgeMessage?>(message);
                }, stoppingToken);
                owner.Result.TrySetResult(result);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
