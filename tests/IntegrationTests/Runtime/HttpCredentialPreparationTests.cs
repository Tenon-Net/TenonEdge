using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class HttpCredentialPreparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndecryptableHttpCredentialFailsPreparationWhileOriginalInputKeepsReadingAndDelivering(bool inputCredential)
    {
        var samples = 0;
        var newRequests = 0;
        var delivered = new ConcurrentQueue<int>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var peer = builder.Build();
        peer.MapGet("/old/sample", () => Results.Json(new { value = Interlocked.Increment(ref samples) }));
        peer.MapPost("/old/deliveries", async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            Assert.Equal("meter", root.GetProperty("deviceKey").GetString());
            Assert.Equal(root.GetProperty("messageId").GetString(), context.Request.Headers["Idempotency-Key"].ToString());
            delivered.Enqueue(root.GetProperty("values").GetProperty("value").GetInt32());
            return Results.NoContent();
        });
        peer.MapMethods("/new/{**path}", ["GET", "POST"], () =>
        {
            Interlocked.Increment(ref newRequests);
            return Results.Json(new { value = -1 });
        });
        await peer.StartAsync(Token);
        var address = new Uri(peer.Urls.Single());
        using var data = TemporaryDirectory.Create();
        using var gate = new CredentialReadGate();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) }, services =>
        {
            // Only stretch the real local resolver's return boundary; do not replace
            // Data Protection, input loops, queue, dispatch, or outbound HTTP.
            services.RemoveAll<IHttpCredentialResolver>();
            services.AddSingleton<IHttpCredentialResolver>(provider =>
            {
                gate.Inner = provider.GetRequiredService<HttpCredentialStore>();
                return gate;
            });
        });
        var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
        var devices = host.Services.GetRequiredService<DeviceDraftStore>();
        var inputs = host.Services.GetRequiredService<InputDraftStore>();
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var credentials = host.Services.GetRequiredService<HttpCredentialStore>();
        var old = connections.Write(null, new("0", "old", "Old", ConnectionKind.Http,
            Http: new(new Uri(address, "old/").AbsoluteUri)), "fixture", null);
        var device = devices.Write(null, new(old.Version, "meter", "Meter", DeviceKind.Http, ConnectionId: old.Connection.Id), "fixture", null);
        var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Sample",
            Http: new("sample", [new("value", "/value", FieldType.Integer)], PeriodMs: 1000)), "fixture", null);
        var saved = routes.Write(null, new(input.Version, "old-route", "Old route", true,
            Graph(input.Input.Id, old.Connection.Id)), "fixture", null);
        Assert.True((await coordinator.ApplyDraftAsync(saved.Version, "fixture", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => delivered.Count >= 3);
        var originalId = RuntimeUnitId.Create("input.http", "meter/sample");
        RuntimeUnitStatus Original() => Assert.Single(coordinator.Runtime.GetStatus().Units, unit => unit.Id == originalId);
        var before = Original().Reads!.ReadsCompleted;

        var added = connections.Write(null, new(saved.Version, "new", "New", ConnectionKind.Http,
            Http: new(new Uri(address, "new/").AbsoluteUri)), "fixture", null);
        var version = added.Version;
        var sourceId = input.Input.Id;
        if (inputCredential)
        {
            var newDevice = devices.Write(null, new(version, "new-meter", "New meter", DeviceKind.Http, ConnectionId: added.Connection.Id), "fixture", null);
            var newInput = inputs.Write(newDevice.Device.Id, null, new(newDevice.Version, "sample", "Sample",
                Http: new("sample", [new("value", "/value", FieldType.Integer)], PeriodMs: 1000)), "fixture", null);
            version = newInput.Version;
            sourceId = newInput.Input.Id;
        }
        saved = routes.Write(null, new(version, "new-route", "New route", true, Graph(sourceId, added.Connection.Id)), "fixture", null);
        const string secret = "fixture-secret-before-damage";
        await credentials.WriteAsync(added.Connection.Id, new(added.Connection.Http!.BaseAddress, HttpCredentialKind.Bearer,
            Secret: secret, ExpectedDraftVersion: saved.Version), "fixture", null, true, Token);
        Assert.Equal(secret, credentials.ResolveHttp(added.Connection.Id, added.Connection.Http.BaseAddress)!.Secret);
        host.Services.GetRequiredService<ConfigDatabase>().InTransaction(client => client.Ado.ExecuteCommand(
            "UPDATE http_credential SET protected_secret = 'fixture-invalid-ciphertext' WHERE connection_id = @id",
            new SqlSugar.SugarParameter("@id", added.Connection.Id)));
        Assert.NotNull(credentials.DescribeHttp(added.Connection.Id));
        Assert.Null(credentials.ResolveHttp(added.Connection.Id, added.Connection.Http.BaseAddress));
        gate.ConnectionId = added.Connection.Id;
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var api = await host.CreateLoggedInClientAsync("operator", Token);
        try
        {
            using var accepted = await api.PostJsonAsync("/api/edge-admin/v1/configuration/apply",
                ConfigurationApplicationEndpointTests.Request(versions.ReviewDraft(saved.Version, Token)), Token);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var id = (await accepted.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
            await OutputPipelineTests.UntilAsync(() => gate.Reached.IsCompleted || coordinator.ReadApplication(id)?.FinishedAt is not null);
            Assert.Equal(RuntimeApplicationStage.Preparing, coordinator.ReadApplication(id)!.Stage);
            Assert.True(gate.Reached.IsCompleted, "Preparation must resolve the real encrypted credential before quiescing old inputs.");
            var heldCount = delivered.Count;
            await OutputPipelineTests.UntilAsync(() => delivered.Count >= heldCount + 3);
            Assert.Equal(1, coordinator.Runtime.ActiveRevision);
            Assert.Equal(RuntimeUnitState.Running, Original().State);
            Assert.True(Original().Reads!.ReadsCompleted >= before + 3);
            Assert.Equal(0, Original().RestartCount);
            Assert.Equal(0, Volatile.Read(ref newRequests));
            gate.Release.Set();
            await OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(id)?.FinishedAt is not null);
            var terminal = await api.GetJsonAsync(accepted.Headers.Location!.OriginalString, Token);
            Assert.Equal("failed", terminal.GetProperty("stage").GetString());
            Assert.False(terminal.GetProperty("committed").GetBoolean());
            Assert.Equal("runtime.prepareFailed", terminal.GetProperty("result").GetProperty("code").GetString());
            var failedRevision = terminal.GetProperty("revision").GetInt64();
            var history = await api.GetJsonAsync($"/api/edge-admin/v1/configuration/versions/{failedRevision}", Token);
            var failed = history.GetProperty("version");
            Assert.Equal(RuntimeSnapshotStates.NotEffective, failed.GetProperty("state").GetString());
            var reason = failed.GetProperty("notEffectiveReason").GetString()!;
            // Output paths have compiler-generated identities, not the canvas node ID.
            Assert.Contains(inputCredential ? "input.http/new-meter/sample" : "output.http/new-route/", reason, StringComparison.Ordinal);
            Assert.Contains("运行单元构建失败，请检查配置和凭证。", reason, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, history.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-invalid-ciphertext", history.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            Assert.Null(versions.ReadPointers().PendingRevision);
            var after = delivered.Count;
            await OutputPipelineTests.UntilAsync(() => delivered.Count >= after + 3);
            Assert.True(Original().Reads!.ReadsCompleted >= before + 6);
            Assert.Equal(RuntimeUnitState.Running, Original().State);
            Assert.Equal(0, Original().RestartCount);
            Assert.Equal(0, Volatile.Read(ref newRequests));
            var received = delivered.ToArray();
            Assert.Equal(Enumerable.Range(1, received.Length), received);

            gate.ConnectionId = 0;
            await credentials.WriteAsync(added.Connection.Id, new(added.Connection.Http.BaseAddress, HttpCredentialKind.Bearer,
                Secret: secret, ExpectedVersion: credentials.Read(added.Connection.Id)!.Version, ExpectedDraftVersion: saved.Version),
                "fixture", null, true, Token);
            var repaired = await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token);
            Assert.True(repaired.Committed && repaired.Activated);
            Assert.True(repaired.Revision > failedRevision);
            await OutputPipelineTests.UntilAsync(() => Volatile.Read(ref newRequests) > 0);
        }
        finally { gate.Release.Set(); }
    }

    private static RouteGraph Graph(long sourceId, long connectionId) => new(1,
        [new("in", "input.http", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { sourceId } })),
         new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId, path = "deliveries" }))],
        [new("in", "out")], new([]));

    private sealed class CredentialReadGate : IHttpCredentialResolver, IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpCredentialStore Inner { get; set; } = null!;
        public long ConnectionId { get; set; }
        public Task Reached => _reached.Task;
        public ManualResetEventSlim Release { get; } = new();
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => Inner.DescribeHttp(connectionId);
        public HttpCredential? ResolveHttp(long connectionId, string recipient)
        {
            var credential = Inner.ResolveHttp(connectionId, recipient);
            if (connectionId == ConnectionId)
            {
                Assert.Null(credential);
                _reached.TrySetResult();
                Assert.True(Release.Wait(TimeSpan.FromSeconds(20), Token), "Credential return boundary was not released.");
            }
            return credential;
        }
        public void Dispose() => Release.Dispose();
    }
}
