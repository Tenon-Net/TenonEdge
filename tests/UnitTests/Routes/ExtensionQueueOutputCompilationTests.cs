using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Routes;

public sealed class ExtensionQueueOutputCompilationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ConnectionModuleDefinition Definition = new("wire", 1, "Wire", new("connection.wire", ParameterScope.Connection,
        [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 253 }]));
    private static ConnectionDraft Connection(bool enabled = true) => new(1, "wire", "Wire", ConnectionKind.Module, enabled, null, null, null,
        new("wire", 1, JsonSerializer.SerializeToElement(new { host = "server.test" })));
    private static RouteCompilationSnapshot Snapshot(IOutputAdapterFactory? factory, bool enabled = true)
    {
        var connections = new ConnectionModuleCatalog([Definition]);
        return new("draft", [], new Dictionary<long, TenonEdge.Host.Features.Devices.DeviceDraft>(),
            new Dictionary<long, TenonEdge.Host.Features.Inputs.InputDraft>(), new Dictionary<long, ConnectionDraft> { [1] = Connection(enabled) },
            new HashSet<long>(), OutputModules: new([], connections, transports: factory is null ? [] : [factory]), ConnectionModules: connections);
    }

    [Fact]
    public void QueueOnlyBindingFreezesConnectionWithoutCreatingSourcesOrExecutingTransport()
    {
        var transport = new Transport();
        var compiled = RoutePlanCompiler.Compile(Snapshot(transport), Token);
        var unit = Assert.Single(compiled.Document.Units);
        Assert.Equal(RuntimeUnitId.Create("output.wire", "queue/wire/sender"), unit.Id);
        Assert.Empty(compiled.Paths);
        Assert.Empty(compiled.Topology.Paths);
        Assert.Empty(compiled.Topology.SourcePathCounts);
        var protocol = OutputModuleCatalog.ReadProtocol(unit)!;
        Assert.True(protocol.Configuration.GetProperty("queueOnly").GetBoolean());
        Assert.Empty(protocol.Configuration.GetProperty("inputs").EnumerateArray());
        Assert.Equal("", OutputTransportRoute.Read(protocol.Configuration, protocol.Id.Kind)!.AddressTemplate);
        var reference = Assert.Single(OutputModuleCatalog.ReadConnections(unit)).Value;
        Assert.Equal(1, reference.ConnectionId);
        Assert.Equal("server.test", ModuleConnectionSnapshot.ReadModuleConfiguration(reference).Parameters.GetProperty("host").GetString());
        Assert.Equal(compiled.Document.ContentHash, RoutePlanCompiler.Compile(Snapshot(transport), Token).Document.ContentHash);
        Assert.Equal(0, transport.Builds);
    }

    [Fact]
    public void DisabledInputOnlyOrUnavailableTransportsDoNotInventSenders()
    {
        var transport = new Transport();
        Assert.Empty(RoutePlanCompiler.Compile(Snapshot(transport, enabled: false), Token).Document.Units);
        Assert.Equal(0, transport.Bindings);
        Assert.Empty(RoutePlanCompiler.Compile(Snapshot(null), Token).Document.Units);
        Assert.Empty(RoutePlanCompiler.Compile(Snapshot(null) with { OutputModules = null }, Token).Document.Units);
        Assert.Empty(RoutePlanCompiler.Compile(Snapshot(new NoQueueTransport()), Token).Document.Units);
    }

    [Fact]
    public void HistoricalReadsNeverRunBindingsOrAddUnfrozenExtensionSenders()
    {
        var transport = new Transport();
        var snapshot = Snapshot(transport);
        var compiled = RoutePlanCompiler.Compile(snapshot, Token).Document;
        var checkpoint = new ConfigurationCheckpoint("frozen", [Connection()], [], [], []);
        var frozen = RuntimeSnapshotDocument.Create(compiled, checkpoint);
        var stored = new RuntimeSnapshotRecord { SchemaVersion = frozen.SchemaVersion, Content = frozen.Content, ContentHash = frozen.ContentHash };
        var bindings = transport.Bindings;
        transport.Fault = "throw";
        Assert.Equal(compiled.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(stored).ContentHash);
        Assert.Equal(frozen.ContentHash, stored.ContentHash);
        Assert.Equal(frozen.Content, stored.Content);
        var empty = RuntimeSnapshotDocument.Create(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), checkpoint);
        Assert.Empty(RuntimeSnapshotDocument.ReadExecutionPlan(new RuntimeSnapshotRecord
        { SchemaVersion = empty.SchemaVersion, Content = empty.Content, ContentHash = empty.ContentHash }).Units);
        Assert.Equal(bindings, transport.Bindings);
        // Reading is not admission; a changed/missing binding cannot be frozen as a new version.
        Assert.Throws<RuntimePlanDocumentException>(() => compiled.ValidateOutputs(snapshot.OutputsCatalog));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("inputs")]
    [InlineData("queueOnly")]
    [InlineData("template")]
    [InlineData("route")]
    [InlineData("mapping")]
    [InlineData("recursive")]
    [InlineData("throw")]
    public void InvalidOrThrowingQueueBindingCannotBecomeAnAppliedPlan(string fault)
    {
        var transport = new Transport { Fault = fault };
        var error = Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(Snapshot(transport), Token));
        Assert.Equal("output.configuration", error.Issue.Code);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, transport.Builds);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("inputs")]
    [InlineData("kind")]
    [InlineData("mapping")]
    public void FrozenQueueEnvelopeRejectsConflictingConnectionOrSourceState(string fault)
    {
        var unit = Assert.Single(RoutePlanCompiler.Compile(Snapshot(new Transport()), Token).Document.Units);
        var config = unit.Configuration.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value);
        switch (fault)
        {
            case "reference":
                config["connectionId"] = 2;
                break;
            case "inputs":
                config["inputs"] = new[] { "meter/source" };
                break;
            case "mapping":
                config["mapping"] = Array.Empty<object>();
                break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new[] { new { kind = fault == "kind" ? "output.other" : unit.Id.Kind, key = unit.Id.Key, configuration = config } },
        });
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(bytes));
    }

    private sealed class NoQueueTransport : IOutputAdapterFactory
    {
        public string Kind => "output.wire";
        public IOutputTarget Build(RuntimeUnitPlan plan) => throw new NotSupportedException();
    }

    private sealed class Transport : IOutputAdapterFactory
    {
        public string Kind => "output.wire";
        public int Builds { get; private set; }
        public int Bindings { get; private set; }
        public string? Fault { get; set; }
        public IOutputTarget Build(RuntimeUnitPlan plan)
        { Builds++; throw new NotSupportedException(); }
        public JsonElement? BindQueueOnly(ModuleConnectionSnapshot connection)
        {
            Bindings++;
            if (Fault == "throw")
            { throw new InvalidOperationException("fixture-secret"); }
            var config = new Dictionary<string, object?>
            {
                ["connectionId"] = Fault == "target" ? 2 : connection.ConnectionId,
                ["inputs"] = Fault == "inputs" ? new[] { "meter/source" } : Array.Empty<string>(),
                ["queueOnly"] = Fault != "queueOnly",
                ["transportRoute"] = new { recipient = "wire://server.test:19001", addressTemplate = Fault == "template" ? "events/{deviceKey}" : "" },
            };
            if (Fault == "route")
            { config.Remove("transportRoute"); }
            if (Fault == "mapping")
            { config["mapping"] = Array.Empty<object>(); }
            if (Fault == "recursive")
            { config["queueOutput"] = new { }; }
            return JsonSerializer.SerializeToElement(config);
        }
    }
}
