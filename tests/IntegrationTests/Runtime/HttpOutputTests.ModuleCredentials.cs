using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Credentials;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Fact]
    public async Task ModuleCredentialsAreRequiredBeforeActivationAndRotationUsesCurrentSecretWithoutApplyingDraft()
    {
        using var data = TemporaryDirectory.Create();
        using var source = Listen();
        using var changedSource = Listen();
        using var sink = Listen();
        var port = ((IPEndPoint)source.LocalEndpoint).Port;
        var settings = new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = ((IPEndPoint)sink.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        };
        CredentialWireModule? module = null;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            services.AddSingleton(ModuleCredentialTests.Definition);
            services.AddSingleton<IInputModuleFactory>(provider => module = new(provider.GetRequiredService<IModuleCredentialResolver>(),
                provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<IOutboundConnector>()));
        });
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var wire = drafts.Write(null, ModuleCredentialTests.Draft(port: port), "fixture", null);
        var output = drafts.Write(null, new(wire.Version, "sink", "Sink", ConnectionKind.Http, Http: new(Base(sink))), "fixture", null);
        var device = host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(output.Version, "meter", "Meter", DeviceKind.Module, ModuleKind: "input.authwire"), "fixture", null);
        var input = host.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "source", "Source", Module: new("input.authwire", 1, JsonSerializer.SerializeToElement(new { }), new() { ["wire"] = wire.Connection.Id })), "fixture", null);
        var graph = new RouteGraph(1,
            [new("in", "input.authwire", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id }))],
            [new("in", "out")], new([]));
        var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null, new(input.Version, "route", "Route", true, graph), "fixture", null);
        var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
        var rejected = await coordinator.ApplyDraftAsync(saved.Version, "fixture", null, Token);
        Assert.False(rejected.Committed);
        Assert.False(rejected.Activated);
        Assert.False(source.Pending());
        Assert.Equal(0, module!.Builds);
        var credentials = host.Services.GetRequiredService<ModuleCredentialStore>();
        var first = await credentials.WriteAsync(wire.Connection.Id, ModuleCredentialTests.Material(saved.Version), "fixture", null, true, Token);
        var applied = await coordinator.ApplyDraftAsync(saved.Version, "fixture", null, Token);
        Assert.True(applied.Activated);
        Assert.Equal(1, module.Builds);
        Assert.Equal("credential.connectionReferenced", Assert.Throws<CredentialWriteException>(() =>
            credentials.Clear(wire.Connection.Id, saved.Version, first.Identity.Version, "fixture", null)).Code);

        async Task<(TcpClient Socket, string Secret)> AcceptFirst()
        {
            var socket = await source.AcceptTcpClientAsync(Token);
            return (socket, await ReadCredential(socket));
        }
        var firstWire = AcceptFirst();
        var firstPost = ServeOnceAsync(sink, 204);
        module.Sample.Release();
        var connected = await firstWire.WaitAsync(TimeSpan.FromSeconds(10), Token);
        using var socket = connected.Socket;
        Assert.Equal(ModuleCredentialTests.Secret, connected.Secret);
        _ = await firstPost.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var rotated = await credentials.WriteAsync(wire.Connection.Id,
            ModuleCredentialTests.Material(saved.Version, first.Identity.Version, "fixture-next-secret", false), "fixture", null, true, Token);
        Assert.Equal(first.Identity.AccountMarker, rotated.Identity.AccountMarker);
        var nextWire = ReadCredential(socket);
        var nextPost = ServeOnceAsync(sink, 204);
        module.Sample.Release();
        Assert.Equal("fixture-next-secret", await nextWire.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var received = await nextPost.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.DoesNotContain("fixture-next-secret", Encoding.UTF8.GetString(received.Body), StringComparison.Ordinal);
        Assert.False(source.Pending()); // Same shared session, no second connection for rotation.
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(applied.Revision, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(saved.Version, drafts.Read().Version);
        var stored = versions.Load(applied.Revision)!;
        Assert.DoesNotContain(ModuleCredentialTests.Secret, stored.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-next-secret", stored.Content, StringComparison.Ordinal);

        var changed = drafts.Write(wire.Connection.Id, ModuleCredentialTests.Draft(saved.Version, ((IPEndPoint)changedSource.LocalEndpoint).Port), "fixture", null);
        _ = await credentials.WriteAsync(wire.Connection.Id, new(changed.Version, rotated.Identity.Version, RetainExisting: true), "fixture", null, true, Token);
        module.Sample.Release();
        await OutputPipelineTests.UntilAsync(() => module.CredentialRejected > 0);
        Assert.False(changedSource.Pending()); // Current credentials never retarget an old frozen input.
        Assert.Equal(applied.Revision, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(stored.ContentHash, versions.Load(applied.Revision)!.ContentHash);
    }

    private static async Task<string> ReadCredential(TcpClient socket)
    {
        var stream = socket.GetStream();
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, Token);
        Assert.InRange(length[0], (byte)1, (byte)128);
        var bytes = new byte[length[0]];
        await stream.ReadExactlyAsync(bytes, Token);
        await stream.WriteAsync(new byte[] { 42 }, Token);
        return Encoding.UTF8.GetString(bytes);
    }
}

internal sealed class CredentialWireModule(IModuleCredentialResolver credentials, ISharedConnections shared, IOutboundConnector outbound) : IInputModuleFactory
{
    private int _builds, _rejected;
    public int Builds => Volatile.Read(ref _builds);
    public int CredentialRejected => Volatile.Read(ref _rejected);
    public SemaphoreSlim Sample { get; } = new(0);
    public string Kind => "input.authwire";
    public int Version => 1;
    public ParameterSchema Parameters { get; } = new("input.authwire", ParameterScope.Input,
        [new("wire", "Wire", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["authwire"] }]);
    public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, null)];
    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
    {
        Interlocked.Increment(ref _builds);
        var frozen = context.Connections["wire"];
        var lease = shared.Acquire(frozen.ConnectionId, frozen.Kind, ModuleConnectionSnapshot.ModuleConfigurationIdentity(frozen), () => new Session(frozen, outbound));
        return new Unit(this, unit.Id, frozen, context, lease, credentials);
    }
    private sealed class Unit(CredentialWireModule owner, RuntimeUnitId id, ModuleConnectionSnapshot frozen, InputUnitContext context,
        IConnectionLease<Session> lease, IModuleCredentialResolver credentials) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await context.WaitForActivationAsync(stoppingToken);
            while (true)
            {
                await owner.Sample.WaitAsync(stoppingToken);
                var credential = credentials.ResolveModule(frozen);
                if (credential is null)
                { Interlocked.Increment(ref owner._rejected); throw new InvalidOperationException("Credential unavailable."); }
                await context.Messages.ProduceAsync(4096, async token =>
                {
                    var value = await lease.ExecuteAsync((session, ct) => session.ReadAsync(credential.GetSecret("auth"), ct), token);
                    var at = context.Time.GetUtcNow();
                    context.Reads.ReadCompleted(at, 0);
                    return new EdgeMessage(Guid.CreateVersion7(at), context.InputKey, context.DeviceKey, at, context.GatewayTimeSource, at,
                        [new("value", FieldValue.FromInt64(value))]);
                }, stoppingToken);
            }
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
    private sealed class Session(ModuleConnectionSnapshot frozen, IOutboundConnector outbound) : IAsyncDisposable
    {
        private Stream? _stream;
        public async Task<long> ReadAsync(string secret, CancellationToken token)
        {
            var parameters = ModuleConnectionSnapshot.ReadModuleConfiguration(frozen).Parameters;
            _stream ??= await outbound.ConnectAsync(parameters.GetProperty("host").GetString()!, parameters.GetProperty("port").GetInt32(), token);
            var bytes = Encoding.UTF8.GetBytes(secret);
            Assert.InRange(bytes.Length, 1, 128);
            await _stream.WriteAsync(new byte[] { (byte)bytes.Length }, token);
            await _stream.WriteAsync(bytes, token);
            var reply = new byte[1];
            await _stream.ReadExactlyAsync(reply, token);
            return reply[0];
        }
        public async ValueTask DisposeAsync() { if (_stream is not null) { await _stream.DisposeAsync(); } }
    }
}
