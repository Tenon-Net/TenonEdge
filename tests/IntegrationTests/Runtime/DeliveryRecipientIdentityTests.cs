using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryRecipientIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatcherFreezesOldIdentityAcrossRestartAndMigrationOnlyReplacesTheRecipient(bool mqtt)
    {
        using var data = TemporaryDirectory.Create();
        var credentials = new Credentials();
        var source = Target(mqtt, 1, credentials);
        StoredDelivery before;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var channel = Channel.CreateBounded<EdgeMessage>(1);
            var state = new InputDispatcherState();
            await using var dispatcher = new InputDispatcherUnit(RuntimeUnitId.Create("input.test", "meter/sample"), channel.Reader,
                [source], state, 1, runtime.Deliveries, TimeProvider.System, new RecordingEventSink());
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var running = dispatcher.RunAsync(stop.Token);
            try
            {
                var at = DateTimeOffset.UtcNow;
                await channel.Writer.WriteAsync(new(Guid.NewGuid(), "sample", "meter", at, TimeSource.GatewayUnsynced, at,
                    new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) }), Token);
                channel.Writer.Complete();
                await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
                var recipient = Assert.IsType<OutputRecipientIdentity>(OutputRecipientIdentity.Read(before.FingerprintSummary));
                Assert.Equal("old-user", recipient.Account);
                Assert.Equal(mqtt ? "mqtt" : "http", recipient.Protocol);
                Assert.Equal(source.Recipient, recipient.Address);
                Assert.Equal(source.CurrentFingerprint, before.Fingerprint);
                Assert.DoesNotContain("fixture-secret", before.FingerprintSummary, StringComparison.Ordinal);
            }
            finally
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            // 旧版摘要与新摘要可以并存，不做读取时回填或隐式数据库更新。
            var legacy = mqtt ? "mqtts://old.test:8883; authentication=password; account=\"old-user\""
                : "https://old.test/api/; authentication=Basic";
            Assert.True((await runtime.Deliveries.EnqueueAsync([new(Guid.NewGuid(), 1, before.SourceMessageId, "meter", 1,
                before.Payload, before.Fingerprint, legacy, before.TargetAddress, before.QualityOfService, before.Headers,
                protocolMetadata: before.ProtocolMetadata)], Token)).Committed);
            await runtime.StopAsync(Token);
        }

        credentials.Username = "new-user";
        credentials.Marker = "new-owner-marker";
        var target = Target(mqtt, 2, credentials);
        var identity = Assert.IsType<OutputTargetIdentity>(target.CaptureIdentity());
        await using var restarted = RuntimeTesting.CreateRuntime([], data.Path);
        restarted.StartCore();
        var originals = restarted.Deliveries.ReadPending(1, 0, 10);
        Assert.Equal(2, originals.Count);
        Assert.Equal(before.FingerprintSummary, originals[0].FingerprintSummary);
        Assert.Equal("old-user", OutputRecipientIdentity.Read(originals[0].FingerprintSummary)?.Account);
        var historical = Assert.IsType<OutputRecipientIdentity>(OutputRecipientIdentity.Read(originals[1].FingerprintSummary));
        Assert.Equal(mqtt, historical.AccountRecorded);
        Assert.Equal(mqtt ? "old-user" : null, historical.Account);
        Assert.False(historical.ClientCertificateRecorded);
        var command = DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 2,
            identity.Fingerprint, identity.Summary,
            originals.Select(record => new DeliveryMigrationItem(record.Seq, record.DeliveryId, record.Fingerprint, record.MigrationId)).ToArray());
        // 本用例核对唯一写者的原子持久化，不声称已完成管理端授权/双发送者/网页迁移。
        var result = await restarted.Deliveries.MigrateAsync(command);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(2, result.AffectedRecords);
        var migrated = restarted.Deliveries.ReadPending(2, 0, 10);
        Assert.Empty(restarted.Deliveries.ReadPending(1, 0, 10));
        Assert.Equal(2, migrated.Count);
        for (var index = 0; index < originals.Count; index++)
        {
            var old = originals[index];
            var current = migrated[index];
            Assert.Equal(old.Seq, current.Seq);
            Assert.Equal(old.DeliveryId, current.DeliveryId);
            Assert.Equal(old.SourceMessageId, current.SourceMessageId);
            Assert.Equal(old.CreatedAt, current.CreatedAt);
            Assert.Equal(old.Payload.ToArray(), current.Payload.ToArray());
            Assert.Equal(old.TargetAddress, current.TargetAddress);
            Assert.Equal(old.QualityOfService, current.QualityOfService);
            Assert.Equal(old.ProtocolMetadata, current.ProtocolMetadata);
            Assert.Equal(old.Headers, current.Headers);
            Assert.Equal(identity.Fingerprint, current.Fingerprint);
            Assert.Equal(identity.Recipient, OutputRecipientIdentity.Read(current.FingerprintSummary));
            Assert.Equal(old.ByteSize - Bytes(old.Fingerprint) - Bytes(old.FingerprintSummary) + Bytes(identity.Fingerprint) + Bytes(identity.Summary), current.ByteSize);
        }
        await restarted.StopAsync(Token);
        await using var final = RuntimeTesting.CreateRuntime([], data.Path);
        final.StartCore();
        Assert.All(final.Deliveries.ReadPending(2, 0, 10), record => Assert.Equal(identity.Recipient, OutputRecipientIdentity.Read(record.FingerprintSummary)));
        Assert.Equal(result.Migration, (await final.Deliveries.MigrateAsync(command)).Migration);
        Assert.Equal(2, final.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static IOutputTarget Target(bool mqtt, long connection, Credentials credentials)
    {
        IOutputAdapterFactory factory = mqtt
            ? new MqttOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new SharedConnections())
            : new HttpOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new SharedConnections());
        object settings = mqtt
            ? new { connectionId = connection, host = connection == 1 ? "old.test" : "new.test", port = 8883, tls = true, usePassword = true, topic = "frozen/topic", inputs = new[] { "meter/sample" } }
            : new { connectionId = connection, baseAddress = connection == 1 ? "https://old.test/api/" : "https://new.test/next/", path = "frozen/path", inputs = new[] { "meter/sample" } };
        credentials.Preparing = true;
        try
        { return factory.Build(new(RuntimeUnitId.Create(factory.Kind, "out"), JsonSerializer.SerializeToElement(settings))); }
        finally { credentials.Preparing = false; }
    }

    private sealed class Credentials : ICredentialResolver, IHttpCredentialResolver
    {
        public bool Preparing;
        public string Username = "old-user", Marker = "old-marker";
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(Username, Marker, "version");
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => new(HttpCredentialKind.Basic, Username, null, Marker, "version");
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(Username, "fixture-secret", Marker, "version");
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => Preparing
            ? new(new(HttpCredentialKind.Basic, Username, null, Marker, "version"), "fixture-secret")
            : throw new InvalidOperationException("Only preparation may resolve credentials in this storage test.");
    }

    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network in this storage test.");
    }
}
