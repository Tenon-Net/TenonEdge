using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputModuleProtocolLifecycleTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DelegationUsesOneProviderPerPrepareOriginalRecoveryGroupAndSupervisorContext(bool compatibleHandoff, bool nested)
    {
        using var data = TemporaryDirectory.Create();
        var provider = new Provider(compatibleHandoff);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.RemoveAll<IRuntimePlanAdapterProvider>();
            services.AddSingleton<IRuntimePlanAdapterProvider>(provider);
            services.AddSingleton<IInputModuleFactory>(new TestProtocolInputModule("mqtt", nested));
            if (nested)
            {
                services.AddSingleton(new ConnectionModuleDefinition("wrapper", 1, "Wrapper", new("connection.wrapper", ParameterScope.Connection,
                    [new("upstream", "Upstream", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["mqtt"] }])));
            }
        });
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var module = runtime.InputModules.WithFrozenFields(new(RuntimeUnitId.Create("input.testmqtt", "module/source"),
            JsonSerializer.Deserialize<JsonElement>(nested ? """
            {"inputModule":{"version":1,"parameters":{"connection":"12","topic":"in/module"},
              "connections":{"connection":{"connectionId":12,"kind":"wrapper","configuration":{
                "connectionModule":{"version":1,"parameters":{"upstream":"11"},"connections":{
                  "upstream":{"connectionId":11,"kind":"mqtt","configuration":{"host":"localhost"}}}}}}}}}
            """ : """
            {"inputModule":{"version":1,"parameters":{"connection":"11","topic":"in/module"},
              "connections":{"connection":{"connectionId":11,"kind":"mqtt","configuration":{"host":"localhost"}}}}}
            """)));
        var builtin = new RuntimeUnitPlan(RuntimeUnitId.Create("input.mqtt", "builtin/source"), InputModuleCatalog.ReadProtocol(module)!.Configuration);
        var before = provider.Creates; // Host startup may prepare an empty plan.
        await using var plan = runtime.Prepare(new(1, [module, builtin]));
        Assert.Equal(before + 1, provider.Creates);
        var factory = provider.Factories.Last();
        if (!compatibleHandoff)
        {
            Assert.Equal(module.Id, Assert.Single(plan.Failures).Id);
            Assert.Single(factory.Contexts);
            return;
        }
        Assert.Empty(plan.Failures);
        Assert.All(plan.Units, unit => Assert.Equal(Provider.Group, unit.RecoveryGroup));
        Assert.Equal(2, factory.Contexts.Count);
        Assert.Equal(0, factory.Starts);
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => factory.Contexts.Count == 3 && factory.Starts == 3);
        Assert.Equal(before + 1, provider.Creates); // Supervisor never creates a second plan provider.
        var rebuilt = factory.Contexts.Where(item => item.Id == module.Id).ToArray();
        Assert.Equal(2, rebuilt.Length);
        Assert.Same(rebuilt[0].Context, rebuilt[1].Context);
        var reference = rebuilt[1].Context.Connections["connection"];
        Assert.Equal(nested ? 12 : 11, reference.ConnectionId);
        if (nested)
        { Assert.Equal(11, ModuleConnectionReferences.Read(reference.Configuration.GetProperty("connectionModule"))["upstream"].ConnectionId); }
        Assert.Equal(1, factory.Disposals);
        Assert.All(runtime.GetStatus().Units.Where(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal)),
            unit => Assert.Equal(RuntimeUnitState.Running, unit.State));
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, factory.Disposals);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        await using var next = runtime.Prepare(new(2, [module, builtin]));
        Assert.Empty(next.Failures);
        Assert.Equal(before + 2, provider.Creates);
        Assert.NotSame(factory, provider.Factories.Last());
        Assert.NotSame(rebuilt[0].Context, provider.Factories.Last().Contexts.First().Context);
    }

    private sealed class Provider(bool handoff) : IRuntimePlanAdapterProvider
    {
        internal static RuntimeUnitId Group { get; } = RuntimeUnitId.Create("connection.mqtt", "11");
        public int Creates { get; private set; }
        public List<Factory> Factories { get; } = [];
        public RuntimePlanAdapters Create()
        {
            Creates++;
            var factory = new Factory(handoff);
            Factories.Add(factory);
            return new([factory], []);
        }
    }

    // Deliberate loop failure at the original protocol-factory seam; no network substitute
    // is claimed here. Real MQTT and Modbus transport tests live in InputModuleProtocolTests.
    private sealed class Factory(bool handoff) : IInputUnitFactory
    {
        public string Kind => "input.mqtt";
        public bool UsesDurableHandoff => handoff;
        public ConcurrentQueue<(RuntimeUnitId Id, InputUnitContext Context)> Contexts { get; } = new();
        private int _starts;
        private int _disposals;
        private int _failed;
        public int Starts => Volatile.Read(ref _starts);
        public int Disposals => Volatile.Read(ref _disposals);
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan unit) => Provider.Group;
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            Assert.False(unit.Configuration.TryGetProperty("inputModule", out _));
            Assert.Equal(11, unit.Configuration.GetProperty("connectionId").GetInt64());
            Contexts.Enqueue((unit.Id, context));
            return new Unit(this, unit.Id);
        }
        private sealed class Unit(Factory owner, RuntimeUnitId id) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                Interlocked.Increment(ref owner._starts);
                if (id.Kind == "input.testmqtt" && Interlocked.Exchange(ref owner._failed, 1) == 0)
                { throw new InvalidOperationException("Test-only first loop failure."); }
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            public ValueTask DisposeAsync()
            { Interlocked.Increment(ref owner._disposals); return ValueTask.CompletedTask; }
        }
    }
}
