using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Outputs;

public sealed class OutputModulePreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly RuntimeUnitPlan Protocol = new(RuntimeUnitId.Create("output.wire", "route/out"),
        JsonSerializer.Deserialize<JsonElement>("""{"transportRoute":{"recipient":"wire://localhost:19001","addressTemplate":"events/{deviceKey}"}}"""));
    private static readonly EdgeMessage Message = new(Guid.NewGuid(), "source", "sensor", DateTimeOffset.UnixEpoch,
        TimeSource.Gateway, DateTimeOffset.UnixEpoch, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(9007199254740993) });
    private static OutputMessageContent Content => new(Encoding.UTF8.GetBytes("wire-format:9007199254740993"), "events/sensor");

    [Fact]
    public void MissingFactoryOrOptionalFormatterIsExplicitlyUnsupportedAndNeverBuildsTargets()
    {
        var disabled = new WithoutFormatter();
        Assert.Null(OutputModuleCatalog.Empty.FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
        Assert.Null(new OutputModuleCatalog([], transports: [disabled]).FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
        // A plan-level kind declaration is discovery metadata, not permission to construct a provider.
        Assert.Null(new OutputModuleCatalog([], planTransportKinds: ["output.wire"]).FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
    }

    [Fact]
    public void FormatterReceivesTheFrozenPlanAndMappedMessageWithoutRewritingItsOutput()
    {
        var id = Guid.NewGuid();
        var content = Content with { QualityOfService = 1, Headers = new Dictionary<string, string> { ["X-Wire"] = "原格式" }, ProtocolMetadata = "v1" };
        var factory = new Formatter((plan, message, deliveryId, cancellationToken) =>
        {
            Assert.Equal(Protocol.Id, plan.Id);
            Assert.Equal(Protocol.ConfigurationHash, plan.ConfigurationHash);
            Assert.Same(Message, message);
            Assert.Equal(id, deliveryId);
            Assert.Equal(Token, cancellationToken);
            return content;
        });
        var formatted = new OutputModuleCatalog([], transports: [factory]).FormatMessage(Protocol, Message, id, Token);
        Assert.NotNull(formatted);
        Assert.Same(content, formatted);
        Assert.DoesNotContain("9007199254740993", formatted.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("qos")]
    [InlineData("utf8")]
    [InlineData("headerName")]
    [InlineData("headerControl")]
    [InlineData("headerNull")]
    [InlineData("headerUtf8")]
    [InlineData("metadataUtf8")]
    public void InvalidFormatterResultsFailClosedWithoutLeakingContent(string invalid)
    {
        var content = invalid switch
        {
            "address" => Content with { Address = "other/fixture-secret" },
            "qos" => Content with { QualityOfService = 2 },
            "utf8" => Content with { Payload = new byte[] { 0xc0, 0xaf } },
            "headerName" => Content with { Headers = new Dictionary<string, string> { [" "] = "fixture-secret" } },
            "headerControl" => Content with { Headers = new Dictionary<string, string> { ["X-Wire"] = "fixture-secret\r\n" } },
            "headerNull" => Content with { Headers = new Dictionary<string, string> { ["X-Wire"] = null! } },
            "headerUtf8" => Content with { Headers = new Dictionary<string, string> { ["X-Wire"] = "\ud800" } },
            "metadataUtf8" => Content with { ProtocolMetadata = "\ud800" },
            _ => throw new ArgumentException(invalid),
        };
        var catalog = Catalog(() => content);
        var error = Assert.Throws<ArgumentException>(() => catalog.FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("body", false)]
    [InlineData("body", true)]
    [InlineData("headerBytes", false)]
    [InlineData("headerBytes", true)]
    [InlineData("headerCount", false)]
    [InlineData("headerCount", true)]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    public void ExactByteAndCountBudgetsAreAcceptedButOneOverIsRejected(string budget, bool over)
    {
        var extra = over ? 1 : 0;
        var content = budget switch
        {
            "body" => Content with { Payload = Encoding.UTF8.GetBytes(new string('界', 87381) + new string('a', 1 + extra)) },
            "headerBytes" => Content with { Headers = new Dictionary<string, string> { ["X"] = new string('界', 5461) + new string('a', extra) } },
            "headerCount" => Content with { Headers = Enumerable.Range(0, 64 + extra).ToDictionary(i => "X-" + i, _ => "value") },
            "metadata" => Content with { ProtocolMetadata = new string('界', 5461) + new string('a', 1 + extra) },
            _ => throw new ArgumentException(budget),
        };
        var catalog = Catalog(() => content);
        if (over)
        { Assert.Throws<InputMessageSizeException>(() => catalog.FormatMessage(Protocol, Message, Guid.NewGuid(), Token)); }
        else
        { Assert.Same(content, catalog.FormatMessage(Protocol, Message, Guid.NewGuid(), Token)); }
    }

    [Fact]
    public void EmptyPayloadIsRejectedAndOneByteIsAccepted()
    {
        Assert.Throws<InputMessageSizeException>(() => Catalog(() => Content with { Payload = ReadOnlyMemory<byte>.Empty })
            .FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
        Assert.Single(Catalog(() => Content with { Payload = new byte[] { 65 } }).FormatMessage(Protocol, Message, Guid.NewGuid(), Token)!.Payload.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrDuringFormattingCannotPublishAResult(bool during)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (!during)
        { cancellation.Cancel(); }
        var catalog = Catalog(() =>
        {
            Assert.True(during, "Cancelled preview must not invoke extension code.");
            cancellation.Cancel();
            return Content;
        });
        Assert.Throws<OperationCanceledException>(() => catalog.FormatMessage(Protocol, Message, Guid.NewGuid(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatterFaultAndUnrelatedCancellationAreSanitized(bool unrelatedCancellation)
    {
        var catalog = Catalog(() => throw (unrelatedCancellation
            ? new OperationCanceledException("fixture-secret") : new InvalidOperationException("fixture-secret")));
        var error = Assert.Throws<ArgumentException>(() => catalog.FormatMessage(Protocol, Message, Guid.NewGuid(), Token));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
    }

    private static OutputModuleCatalog Catalog(Func<OutputMessageContent?> format) => new([], transports: [new Formatter((_, _, _, _) => format())]);
    private sealed class WithoutFormatter : IOutputAdapterFactory
    {
        public string Kind => "output.wire";
        public IOutputTarget Build(RuntimeUnitPlan plan) => throw new InvalidOperationException("Preview must not build a runtime target.");
    }
    private sealed class Formatter(Func<RuntimeUnitPlan, EdgeMessage, Guid, CancellationToken, OutputMessageContent?> format) : IOutputAdapterFactory
    {
        public string Kind => "output.wire";
        public IOutputTarget Build(RuntimeUnitPlan plan) => throw new InvalidOperationException("Preview must not build a runtime target.");
        public OutputMessageContent? FormatMessage(RuntimeUnitPlan plan, EdgeMessage message, Guid deliveryId, CancellationToken cancellationToken) =>
            format(plan, message, deliveryId, cancellationToken);
    }
}
