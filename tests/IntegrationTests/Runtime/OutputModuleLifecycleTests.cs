using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputModuleLifecycleTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("id")]
    [InlineData("connection")]
    [InlineData("inputs")]
    public async Task ModuleUsesOnlyThisPreparesProviderAndChecksOriginalTargetIdentity(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        var provider = new Provider(scenario);
        var module = new TestProtocolOutputModule("http");
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.RemoveAll<IOutputAdapterFactory>();
            services.RemoveAll<IRuntimePlanAdapterProvider>();
            services.AddSingleton<IRuntimePlanAdapterProvider>(provider);
            services.AddSingleton<IOutputModuleFactory>(module);
        });
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var unit = module.Unit(JsonSerializer.SerializeToElement(new { baseAddress = "http://localhost/" }));
        var native = OutputModuleCatalog.ReadProtocol(unit)!;
        var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.simulator", "meter/source"), JsonSerializer.SerializeToElement(new { periodMs = 1000, seed = 42 }));
        var before = provider.Creates;
        await using var prepared = runtime.Prepare(new(1, [input, unit, native]));
        Assert.Equal(before + 1, provider.Creates);
        Assert.Equal(2, provider.Last!.Builds.Count);
        Assert.Contains(provider.Last.Builds, id => id == unit.Id);
        if (scenario == "valid")
        {
            Assert.Empty(prepared.Failures);
            Assert.Equal(2, prepared.Outputs.Count);
            Assert.Same(prepared.Outputs[0].State, prepared.Outputs[1].State);
            Assert.All(prepared.Outputs, output => Assert.Equal("output.http", output.ProtocolKind));
        }
        else
        { Assert.Contains(prepared.Failures, failure => failure.Id == unit.Id); }
        Assert.Equal(0, provider.Last.Creates);
        var first = provider.Last;
        await using var next = runtime.Prepare(new(2, [input, unit]));
        Assert.Equal(before + 2, provider.Creates);
        Assert.NotSame(first, provider.Last);
        Assert.Equal(0, provider.Last.Creates);
    }

    private sealed class Provider(string scenario) : IRuntimePlanAdapterProvider
    {
        internal int Creates { get; private set; }
        internal Factory? Last { get; private set; }
        public RuntimePlanAdapters Create()
        {
            Creates++;
            Last = new(scenario);
            return new([], [Last]);
        }
    }
    private sealed class Factory(string scenario) : IOutputAdapterFactory
    {
        public string Kind => "output.http";
        internal List<RuntimeUnitId> Builds { get; } = [];
        internal int Creates { get; private set; }
        public IOutputTarget Build(RuntimeUnitPlan plan)
        {
            Assert.False(plan.Configuration.TryGetProperty("outputModule", out _));
            Builds.Add(plan.Id);
            return new Target(this, plan.Id, scenario);
        }
        private sealed class Target(Factory owner, RuntimeUnitId id, string scenario) : IOutputTarget
        {
            public RuntimeUnitId Id => scenario == "id" ? RuntimeUnitId.Create("output.http", "wrong") : id;
            public long ConnectionId => scenario == "connection" ? 12 : 1;
            public IReadOnlyList<string> Inputs => scenario == "inputs" ? ["other/source"] : ["meter/source"];
            public string? ClientId => null;
            public string Recipient => "http://localhost/";
            public string CredentialVersion => "none";
            public string CurrentFingerprint => "fixed";
            public bool CanShareSenderWith(IOutputTarget other) => other is Target;
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => throw new InvalidOperationException("Preparation must not generate messages.");
            public IOutputAdapter CreateAdapter()
            {
                owner.Creates++;
                throw new InvalidOperationException("Preparation must not start senders.");
            }
        }
    }
}
