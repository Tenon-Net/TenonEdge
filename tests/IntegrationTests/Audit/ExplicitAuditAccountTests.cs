using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Audit;

/// <summary>已经捕获的非秘密身份经过原存储/协调者到审计查询；不冒充 Bearer 认证或 HTTPS 验收。</summary>
public sealed class ExplicitAuditAccountTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly AuditAccount Actor = new("operator", "MES configuration");
    private const string Ip = "192.0.2.33";

    [Fact]
    public async Task DraftCreateUpdateCopyCsvAndDeleteKeepTokenIdentityInTheirAtomicAudits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var inputs = factory.Services.GetRequiredService<InputDraftStore>();
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var connectionRequest = new ConnectionDraftRequest("0", "plc", "PLC", ConnectionKind.Modbus, false, Modbus: new("10.0.0.1"));
        var connection = connections.Write(null, connectionRequest, Actor, Ip);
        connection = connections.Write(connection.Connection.Id, connectionRequest with { ExpectedVersion = connection.Version, Name = "PLC 2" }, Actor, Ip);
        var deviceRequest = new DeviceDraftRequest(connection.Version, "meter", "Meter", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1);
        var device = devices.Write(null, deviceRequest, Actor, Ip);
        device = devices.Write(device.Device.Id, deviceRequest with { ExpectedVersion = device.Version, Name = "Meter 2" }, Actor, Ip);
        var inputRequest = new InputDraftRequest(device.Version, "fast", "Fast", Modbus: new(1000,
            [new() { Key = "value", Address = 0, FunctionCode = 3, Type = "uint16" }]));
        var input = inputs.Write(device.Device.Id, null, inputRequest, Actor, Ip);
        input = inputs.Write(device.Device.Id, input.Input.Id, inputRequest with { ExpectedVersion = input.Version, Name = "Fast 2" }, Actor, Ip);
        var imported = inputs.ImportPoints(device.Device.Id, new(input.Version, PointCsvMode.Overwrite, PointCsvEncoding.Utf8,
            Encoding.UTF8.GetBytes("inputKey,key,name,functionCode,address,addressNotation,type,bit,byteOrder,unit,enabled\nfast,value,Value,3,1,offset,uint16,,,,true\n")), Actor, Ip);
        Assert.Equal(1, imported.Updated);
        var copied = devices.Copy(device.Device.Id, new(imported.Version, 1), Actor, Ip);
        var copy = Assert.Single(copied.Devices);
        var deletedCopy = devices.Delete(copy.Id, copied.Version, Actor, Ip);
        var deletedInput = inputs.Delete(device.Device.Id, input.Input.Id, deletedCopy.Version, Actor, Ip);
        var deletedDevice = devices.Delete(device.Device.Id, deletedInput.Version, Actor, Ip);
        var deletedConnection = connections.Delete(connection.Connection.Id, deletedDevice.Version, Actor, Ip);
        var routeRequest = new RouteDraftRequest(deletedConnection.Version, "route", "Route", false, new(1, [], [], new([])));
        var route = routes.Write(null, routeRequest, Actor, Ip);
        route = routes.Write(route.Route.Id, routeRequest with { ExpectedVersion = route.Version, Name = "Route 2" }, Actor, Ip);
        var routeCopy = routes.Copy(route.Route.Id, new(route.Version, "copy", "Copy"), Actor, Ip);
        var deletedRouteCopy = routes.Delete(routeCopy.Route.Id, routeCopy.Version, Actor, Ip);
        routes.Delete(route.Route.Id, deletedRouteCopy.Version, Actor, Ip);

        await AssertAudits(factory, ["connection.draftCreated", "connection.draftUpdated", "connection.draftDeleted",
            "device.draftCreated", "device.draftUpdated", "device.draftCopied", "device.draftDeleted", "device.draftDeleted",
            "input.draftCreated", "input.draftUpdated", "input.draftDeleted", "input.pointsImported",
            "route.draftCreated", "route.draftUpdated", "route.draftCopied", "route.draftDeleted", "route.draftDeleted"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialWriteClearAndConnectionDeletionNeverDropTheTokenName(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new("0", "remote", "Remote", mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
            false, Mqtt: mqtt ? new("10.0.0.1") : null, Http: mqtt ? null : new("http://10.0.0.1/")), Actor, Ip);
        if (mqtt)
        {
            var store = factory.Services.GetRequiredService<MqttCredentialStore>();
            var request = new MqttCredentialRequest("mqtt://10.0.0.1:1883", "receiver", "fixture-secret", ExpectedDraftVersion: connection.Version);
            var saved = store.Write(connection.Connection.Id, request, Actor, Ip, true);
            store.Clear(connection.Connection.Id, connection.Version, saved.Version, Actor, Ip);
            store.Write(connection.Connection.Id, request, Actor, Ip, true);
        }
        else
        {
            var store = factory.Services.GetRequiredService<HttpCredentialStore>();
            var request = new HttpCredentialRequest("http://10.0.0.1/", HttpCredentialKind.Basic, Username: "receiver", Secret: "fixture-secret", ExpectedDraftVersion: connection.Version);
            var saved = store.Write(connection.Connection.Id, request, Actor, Ip, true);
            store.Clear(connection.Connection.Id, connection.Version, saved.Version, Actor, Ip);
            store.Write(connection.Connection.Id, request, Actor, Ip, true);
        }
        connections.Delete(connection.Connection.Id, connection.Version, Actor, Ip);
        var protocol = mqtt ? "mqtt" : "http";
        await AssertAudits(factory, ["connection.draftCreated", $"credential.{protocol}.write", $"credential.{protocol}.clear",
            $"credential.{protocol}.write", "credential.connectionDeleted", "connection.draftDeleted"]);
    }

    [Fact]
    public async Task BackgroundApplyRollbackAndOtherConfigurationWritesKeepExplicitIdentity()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.True((await coordinator.ApplyDraftAsync("0", Actor, Ip, Token)).Activated);
        Assert.True((await coordinator.ApplyDraftAsync("0", Actor, Ip, Token)).Activated);
        Assert.True((await coordinator.RollbackAsync(1, "0", Actor, Ip, Token)).Activated);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var added = devices.Write(null, new(devices.Read().Version, "meter", "Meter", DeviceKind.Simulator), Actor, Ip);
        versions.DiscardDraft(added.Version, 3, Actor, Ip, Token);
        versions.MarkRetry(versions.ReadRetryCheckpoint(3, RuntimeSnapshotDocument.ReadExecutionPlan(versions.Load(3)!).ContentHash), Actor, Ip);
        var policy = factory.Services.GetRequiredService<OutboundPolicyStore>();
        policy.Write(new([], [1883]), Actor, Ip);
        var controls = factory.Services.GetRequiredService<OutputControlStore>();
        var paused = controls.Write(123, new(true, true), Actor, Ip);
        controls.Write(123, new(false, true, paused.ResumeVersion), Actor, Ip);
        await AssertAudits(factory, [AuditActions.SnapshotApplied, AuditActions.SnapshotApplied, AuditActions.SnapshotRolledBack,
            "device.draftCreated", AuditActions.DraftDiscarded, AuditActions.RuntimeRetried,
            "system.outboundPolicyChanged", "delivery.pause", "delivery.resume"]);
    }

    private static async Task AssertAudits(TenonEdgeApplicationFactory factory, string[] expected)
    {
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        using var response = await client.GetAsync("/api/edge-admin/v1/audit?account=operator&limit=200", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.ReadJsonAsync(Token);
        var records = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expected.Order(), records.Select(record => record.GetProperty("action").GetString()).Order());
        Assert.All(records, record =>
        {
            Assert.Equal("MES configuration", record.GetProperty("tokenName").GetString());
            Assert.Equal(Ip, record.GetProperty("sourceIp").GetString());
            Assert.DoesNotContain("fixture-secret", record.GetRawText(), StringComparison.Ordinal);
        });
    }
}
