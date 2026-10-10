using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class MqttInputHandoffTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task PrivacyBlockedCacheDoesNotChangeBusinessHandoff(bool sensitive, int qos)
    {
        var harness = new Harness();
        var credential = new MqttCredential("fixture", "fixture/secret", "account", "old-generation");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { value = 42, echo = sensitive ? credential.Password : "safe" });
        var pending = harness.Receive(payload, (MqttQualityOfServiceLevel)qos, credential);
        var edge = await harness.Channel.HoldingReader.ReadAsync(Token);
        Assert.Equal("42", edge.Values["value"].ToString());
        Assert.Equal(sensitive ? DiagnosticSampleAccess.Redacted : DiagnosticSampleAccess.Safe, edge.DiagnosticAccess);
        Assert.Equal(sensitive ? credential.Password : "safe", edge.RawJson!.Root.GetProperty("echo").GetString());
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var id = RuntimeUnitId.Create("input.mqtt", "device/mqtt");
        var sequence = cache.CaptureInput(id, 1, edge);
        var record = Assert.Single(cache.Read(id, true));
        Assert.Equal(sensitive, record.PrivacyBlocked);
        Assert.Equal(!sensitive, cache.TryReadComplete(id, sequence, 1, edge.SourceMessageId, out _));
        if (sensitive)
        { Assert.Null(record.Body); Assert.Equal(0, record.StoredBytes); }
        harness.Channel.Release(edge, InputHandoffStatus.Committed);
        Assert.Equal(qos == 0 ? InputHandoffStatus.Buffered : InputHandoffStatus.Committed, await pending);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task DecodesExactFieldsAndOwnsRawAfterSourceBufferChangesButStillWaitsForCommit()
    {
        var harness = new Harness();
        var bytes = "{\"value\":9223372036854775807,\"extra\":\"raw\"}"u8.ToArray();
        var pending = harness.Receive(bytes);
        var edge = await harness.Channel.HoldingReader.ReadAsync(Token);
        Array.Clear(bytes);
        Assert.Equal("9223372036854775807", edge.Values["value"].ToString());
        Assert.Equal("raw", edge.RawJson!.Root.GetProperty("extra").GetString());
        Assert.Equal(TimeSource.GatewayUnsynced, edge.TimeSource);
        Assert.Equal(edge.ReceivedAt, edge.CollectedAt);
        Assert.False(pending.IsCompleted);
        Assert.True(harness.Budget.Status.ReservedBytes > 0);
        harness.Channel.Release(edge, InputHandoffStatus.Committed);
        Assert.Equal(InputHandoffStatus.Committed, await pending);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("secret-invalid-json")]
    [InlineData("{} {}")]
    [InlineData("{\"value\":1,}")]
    public async Task InvalidDocumentIsExplicitDiscardWithoutLeakingBody(string body)
    {
        var harness = new Harness();
        Assert.Equal(InputHandoffStatus.Rejected, await harness.Receive(Encoding.UTF8.GetBytes(body)));
        Assert.False(harness.Channel.Reader.TryRead(out _));
        Assert.Equal(1, harness.Reads.Snapshot().DataRejected);
        Assert.DoesNotContain("secret", harness.Reads.Snapshot().LastError!, StringComparison.Ordinal);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task InvalidUtf8AndOversizedAndDeepDocumentsAreDiscarded()
    {
        var harness = new Harness();
        foreach (var bytes in new byte[][]
        {
            [123, 34, 118, 34, 58, 34, 255, 34, 125],
            new byte[MqttInputReceiver.MaximumPayloadBytes + 1],
            Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)),
        })
        {
            Assert.Equal(InputHandoffStatus.Rejected, await harness.Receive(bytes));
        }
        Assert.Equal(3, harness.Reads.Snapshot().DataRejected);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task InvalidFieldCreatesQualityNotFabricatedValue()
    {
        var harness = new Harness();
        var pending = harness.Receive("{\"value\":\"bad\"}"u8.ToArray());
        var edge = await harness.Channel.HoldingReader.ReadAsync(Token);
        Assert.Empty(edge.Values);
        Assert.Equal(QualityCode.ConversionFailed, edge.Quality["value"]);
        harness.Channel.Release(edge, InputHandoffStatus.Rejected);
        Assert.Equal(InputHandoffStatus.Rejected, await pending);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05Z", false)]
    [InlineData("invalid-time", true)]
    public async Task ExplicitUpstreamTimeIsResolvedOrFallsBackWithDiagnostic(string timestamp, bool invalid)
    {
        var harness = new Harness(JsonSerializer.Deserialize<JsonElement>(
            """{"path":"/at","format":"rfc3339"}"""));
        var pending = harness.Receive(JsonSerializer.SerializeToUtf8Bytes(new { value = 42, at = timestamp }));
        var edge = await harness.Channel.HoldingReader.ReadAsync(Token);
        if (invalid)
        {
            Assert.Equal(edge.ReceivedAt, edge.CollectedAt);
            Assert.Equal(TimeSource.GatewayUnsynced, edge.TimeSource);
        }
        else
        {
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), edge.CollectedAt);
            Assert.Equal(TimeSource.Upstream, edge.TimeSource);
        }
        harness.Channel.Release(edge, InputHandoffStatus.Committed);
        Assert.Equal(InputHandoffStatus.Committed, await pending);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task QosZeroDropsImmediatelyWhenChannelFullWithoutParsingOrWaitingForCommit()
    {
        var harness = new Harness();
        Assert.Equal(InputHandoffStatus.Buffered, await harness.Receive("{\"value\":1}"u8.ToArray(), MqttQualityOfServiceLevel.AtMostOnce));
        var occupied = harness.Budget.Status.ReservedBytes;
        var dropped = harness.Receive("invalid-json"u8.ToArray(), MqttQualityOfServiceLevel.AtMostOnce);
        Assert.True(dropped.IsCompletedSuccessfully);
        Assert.Equal(InputHandoffStatus.Rejected, await dropped);
        Assert.Equal(1, harness.Reads.Snapshot().CyclesSkipped);
        Assert.Equal(0, harness.Reads.Snapshot().DataRejected);
        Assert.Equal(occupied, harness.Budget.Status.ReservedBytes);
        Assert.True(harness.Channel.Reader.TryRead(out var edge));
        Assert.Equal("1", edge.Values["value"].ToString());
        Assert.False(harness.Channel.HasHandoff(edge));
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task QosZeroDropsImmediatelyWhenBudgetUnavailableAndRecoversAfterRelease()
    {
        var harness = new Harness();
        Assert.True(harness.Budget.TryReserve(harness.Budget.Capacity));
        var dropped = harness.Receive("{}"u8.ToArray(), MqttQualityOfServiceLevel.AtMostOnce);
        Assert.True(dropped.IsCompletedSuccessfully);
        Assert.Equal(InputHandoffStatus.Rejected, await dropped);
        Assert.Equal(1, harness.Reads.Snapshot().CyclesSkipped);
        Assert.Equal(0, harness.Reads.Snapshot().ReadsCompleted);
        harness.Budget.Release(harness.Budget.Capacity);
        Assert.Equal(InputHandoffStatus.Buffered, await harness.Receive("{}"u8.ToArray(), MqttQualityOfServiceLevel.AtMostOnce));
        Assert.True(harness.Channel.Reader.TryRead(out _));
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    private sealed class Harness
    {
        public InFlightBudget Budget { get; } = new(64L * 1024 * 1024);
        public BudgetedInputChannel Channel { get; }
        public InputReadStatus Reads { get; } = new("device", "mqtt", new RecordingEventSink());
        private readonly MqttInputHandoff _handoff;
        public Harness(JsonElement upstreamTime = default)
        {
            Channel = new(1, Budget) { UsesDurableHandoff = true };
            var context = new InputUnitContext("device", "mqtt", new FakeTimeProvider(),
                new FixedClockSyncStatus(ClockSyncState.NotSynchronized), Reads, (InputMessageWriter)Channel.Writer);
            _handoff = new(context, new JsonInputDecoderFactory().Build(JsonSerializer.Deserialize<JsonElement>(
                """[{"key":"value","path":"/value","type":"integer"}]"""), upstreamTime));
        }
        public Task<InputHandoffStatus> Receive(byte[] payload, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce, MqttCredential? credential = null) =>
            _handoff.ReceiveAsync(new MqttApplicationMessageBuilder().WithTopic("input").WithPayload(payload).WithQualityOfServiceLevel(qos).Build(), credential, Token);
    }
}
