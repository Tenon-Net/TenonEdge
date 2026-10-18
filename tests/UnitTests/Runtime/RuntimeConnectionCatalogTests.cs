using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.UnitTests.Runtime;

public sealed class RuntimeConnectionCatalogTests
{
    [Theory]
    [InlineData(false, false, 0, false, "unused")]
    [InlineData(false, false, -1, false, "unknown")]
    [InlineData(false, true, 0, false, "connecting")]
    [InlineData(false, false, 1, false, "connecting")]
    [InlineData(false, true, 0, true, "degraded")]
    [InlineData(true, true, 1, false, "disabled")]
    public void UsageAndLifecycleNeverInventSuccessfulCommunication(bool disabled, bool users, long pending, bool unavailable, string expected)
    {
        Assert.Equal(expected, RuntimeConnectionEndpoints.State(new("http", "HTTP", "http", "http://example.test/", !disabled),
            users, pending < 0 ? null : pending, null, unavailable));
        Assert.Equal("notApplied", RuntimeConnectionEndpoints.State(null, users, pending, null, unavailable));
    }

    [Theory]
    [InlineData(ConnectionCommunicationState.Connecting, "connecting")]
    [InlineData(ConnectionCommunicationState.Connected, "connected")]
    [InlineData(ConnectionCommunicationState.Degraded, "degraded")]
    public void ActualObservationIsRequiredAndClosingOverridesIt(ConnectionCommunicationState state, string expected)
    {
        var definition = new ConnectionListDefinition("modbus", "Modbus", "modbus", "localhost:502", true);
        var observation = new SharedConnectionObservation(false, new(state));
        Assert.Equal(expected, RuntimeConnectionEndpoints.State(definition, true, 0, observation, false));
        Assert.Equal("degraded", RuntimeConnectionEndpoints.State(definition, true, 0, observation with { Closing = true }, false));
        Assert.Equal("unknown", RuntimeConnectionEndpoints.State(definition, true, 0, new(false, null), false));
        Assert.Equal("unused", RuntimeConnectionEndpoints.State(definition, false, null, observation, false));
    }

    [Fact]
    public void StoredMissingModulesStillExposeReferencesWithoutExecutingModuleCodeAndDeduplicateSameConnection()
    {
        var connection = new ConnectionDraft(1, "http", "HTTP", ConnectionKind.Http, true, null, null, new("http://example.test/"));
        var device = new DeviceDraft(2, "module", "Module device", DeviceKind.Module, null, null, null, ModbusAddressNotation.Offset, true, "input.custom.fixture");
        var source = new InputDraft(3, 2, "read", "Read", DeviceKind.Module, true, null,
            Module: new("input.custom.fixture", 9, JsonSerializer.SerializeToElement(new { }), new() { ["a"] = 1, ["b"] = 1 }));
        var graph = new RouteGraph(1, [new("out", "output.custom.fixture", 9, "Output", JsonSerializer.SerializeToElement(new
        { connectionId = 1, parameters = new { }, connections = new { a = 1, b = 1 } }))], [], new([]));
        var route = new RouteDraft(4, "route", "Route", null, false, graph, null, 0);
        var checkpoint = new ConfigurationCheckpoint("v", [connection], [device], [source], [route]);
        var users = RuntimeConnectionCatalog.ReadUsers(checkpoint)[1];
        Assert.Equal(2, users.Length);
        Assert.Contains(users, user => user.Kind == "input" && user.Key == "module/read" && user.Enabled && !user.Active);
        Assert.Contains(users, user => user.Kind == "output" && user.Key == "route/out" && !user.Enabled && !user.Active);
        var entry = Assert.Single(RuntimeConnectionCatalog.Read(null, checkpoint, Guid.Empty.ToString("D"))).Value;
        Assert.Equal(users, entry.Users);
        Assert.Empty(entry.Identities);
        Assert.Empty(entry.Units);
    }

    [Fact]
    public void FullDraftComparisonIncludesHiddenParametersAndIgnoresHeaderOrder()
    {
        var current = new ConnectionDraft(1, "http", "HTTP", ConnectionKind.Http, true, null, null,
            new("http://example.test/", 5000, new() { ["X-A"] = "a", ["X-B"] = "b" }));
        Assert.False(RuntimeConnectionCatalog.DraftChanged(current, current with
        { Http = current.Http! with { Headers = new() { ["X-B"] = "b", ["X-A"] = "a" } } }));
        Assert.True(RuntimeConnectionCatalog.DraftChanged(current, current with { Http = current.Http! with { TimeoutMs = 6000 } }));
        Assert.True(RuntimeConnectionCatalog.DraftChanged(current, current with { Http = current.Http! with { Headers = new() { ["X-A"] = "changed" } } }));
        Assert.True(RuntimeConnectionCatalog.DraftChanged(current, null));
        Assert.True(RuntimeConnectionCatalog.DraftChanged(null, current));
        Assert.False(RuntimeConnectionCatalog.DraftChanged(null, null));
    }

    [Fact]
    public void LegacyPlanCatalogHasRealFrozenTargetsAndNoInventedBusinessKeys()
    {
        var plan = RuntimePlanDocument.Parse("""
            {"schemaVersion":1,"units":[{"kind":"input.http","key":"meter/read","configuration":{
            "connectionId":3,"baseAddress":"http://example.test/","fields":[{"key":"v","path":"/v","type":"integer"}]}}]}
            """u8);
        var entry = Assert.Single(RuntimeConnectionCatalog.Read(plan, null, Guid.Empty.ToString("D"))).Value;
        Assert.Null(entry.Definition.Key);
        Assert.Equal("http://example.test/", entry.Definition.Target);
        Assert.Equal("meter/read", Assert.Single(entry.Users).Key);
        Assert.True(entry.Users[0].Active);
        Assert.Equal("http", Assert.Single(entry.Identities).Protocol);
    }
}
