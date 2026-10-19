using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Routes;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Fact]
    public async Task OutputModuleFreezesHttpAccountAndQuarantinesOriginalDurablePayloadWhenAccountChanges()
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        var module = new TestProtocolOutputModule("http");
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IOutputModuleFactory>(module));

        var seed = RouteOutputModuleTests.Seed(host.Services, "http", port, map: false);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var connection = Assert.Single(drafts.Read().Connections);
        var credentials = host.Services.GetRequiredService<HttpCredentialStore>();
        var firstCredential = await credentials.WriteAsync(connection.Id,
            new($"http://127.0.0.1:{port}/", HttpCredentialKind.Basic, Username: "receiver-A", Secret: "first-secret",
                ExpectedDraftVersion: seed.ExpectedVersion), "fixture", null, true, token: Token);
        var firstAccountMarker = credentials.DescribeHttp(connection.Id)!.AccountMarker;
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var route = routes.Write(null, new(seed.ExpectedVersion, "module-account", "Module account", true, seed.Graph), "fixture", null);
        var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(route.Version, "fixture", null, Token);
        Assert.True(applied.Activated);

        var response = ServeOnceAsync(server, 401);
        var received = await response.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var firstAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("receiver-A:first-secret"));
        Assert.Contains(firstAuthorization, received.Header, StringComparison.Ordinal);
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => cache.ReadPending(connection.Id, 0, 10).Count == 1);
        var frozen = Assert.Single(cache.ReadPending(connection.Id, 0, 10));
        var oldIdentity = runtime.CaptureMigrationTarget(connection.Id, applied.Revision)!;
        Assert.Equal("receiver-A", oldIdentity.Recipient.Account);
        Assert.Equal(oldIdentity.Fingerprint, frozen.Fingerprint);
        Assert.Equal(oldIdentity.Recipient, OutputRecipientIdentity.Read(frozen.FingerprintSummary));
        Assert.Equal(frozen.Payload.ToArray(), received.Body);
        using (var payload = JsonDocument.Parse(frozen.Payload))
        { Assert.Equal(frozen.DeliveryId, payload.RootElement.GetProperty("messageId").GetGuid()); }

        var changed = await credentials.WriteAsync(connection.Id,
            new($"http://127.0.0.1:{port}/", HttpCredentialKind.Basic, Username: "receiver-B", Secret: "second-secret",
                ExpectedVersion: firstCredential.Version, AccountChanged: true, ExpectedDraftVersion: route.Version),
            "fixture", null, true, token: Token);
        Assert.NotEqual(firstCredential.Version, changed.Version);
        Assert.NotEqual(firstAccountMarker, credentials.DescribeHttp(connection.Id)!.AccountMarker);
        var currentIdentity = runtime.CaptureMigrationTarget(connection.Id, applied.Revision)!;
        Assert.Equal("receiver-B", currentIdentity.Recipient.Account);
        Assert.NotEqual(oldIdentity.Fingerprint, currentIdentity.Fingerprint);

        var controls = host.Services.GetRequiredService<OutputControlStore>();
        var control = controls.Read(connection.Id);
        _ = controls.Write(connection.Id, new(false, true, control.ResumeVersion), "fixture", null);
        await OutputPipelineTests.UntilAsync(() => cache.ReadQuarantined(connection.Id, 0, 10).Count == 1);
        var isolated = Assert.Single(cache.ReadQuarantined(connection.Id, 0, 10));
        Assert.Equal(frozen.Payload.ToArray(), isolated.Payload.ToArray());
        Assert.Equal(frozen.DeliveryId, isolated.DeliveryId);
        Assert.Equal(frozen.Seq, isolated.Seq);
        Assert.False(server.Pending()); // The old payload was not sent to receiver-B.
        Assert.Equal(applied.Revision, host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
        Assert.Equal(route.Version, drafts.Read().Version);
    }
}
