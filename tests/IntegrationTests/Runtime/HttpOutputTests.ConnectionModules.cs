using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    private static readonly string[] WireInputs = ["meter/read", "second/read"];
    [Fact]
    public async Task NewConnectionModuleSharesRealTransportAndMissingDefinitionOnlyStopsItsInputsOnRestart()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var source = Listen();
        using var sink = Listen();
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllBytesAsync(seed, WireDocument(source, Base(sink)), Token);
        var settings = new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)source.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = ((IPEndPoint)sink.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        };
        var wire = ServeWireAsync(source);
        var firstPost = ServeOnceAsync(sink, 401);
        TestWireModule? module = null;
        string hash;
        Dictionary<Guid, byte[]> frozen;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string>(settings) { [RuntimeSettings.SeedSnapshotKey] = seed }, services => Register(services, true, true, value => module = value)))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            _ = await firstPost.WaitAsync(TimeSpan.FromSeconds(15), Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 3).Count == 2 &&
                module?.Reads == 2);
            Assert.NotNull(module);
            Assert.Equal(1, module.Sessions);
            Assert.Equal(2, module.Builds);
            Assert.False(source.Pending()); // both input instances borrowed the single accepted connection
            var records = runtime.Deliveries.ReadPending(1, 0, 3);
            Assert.Equal(["meter", "second"], records.Select(record => record.DeviceKey).Order());
            Assert.All(records, record =>
            {
                using var body = JsonDocument.Parse(record.Payload);
                Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
                Assert.Equal(record.DeliveryId.ToString(), body.RootElement.GetProperty("messageId").GetString());
            });
            frozen = records.ToDictionary(record => record.DeliveryId, record => record.Payload.ToArray());
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            hash = snapshot.ContentHash;
            var catalog = RuntimeConnectionCatalog.Read(RuntimeSnapshotDocument.ReadPlan(snapshot), null,
                host.Services.GetRequiredService<GatewayIdentity>().NodeId);
            Assert.Equal(2, catalog[10].Users.Count);
            var parameters = Assert.Single(catalog[10].Identities);
            Assert.Equal(ConnectionCommunicationState.Connected, host.Services.GetRequiredService<ISharedConnections>()
                .Observe(10, parameters.Protocol, parameters.Identity)!.Observation!.State);
            TestAccounts.Create(host.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
            using var client = await host.CreateLoggedInClientAsync("viewer", Token);
            var response = await client.GetJsonAsync("/api/edge-admin/v1/runtime/connections", Token);
            var entry = Assert.Single(response.GetProperty("connections").EnumerateArray(), value => value.GetProperty("id").GetInt64() == 10);
            Assert.Equal("testwire", entry.GetProperty("effective").GetProperty("kind").GetString());
            Assert.Equal("connected", entry.GetProperty("state").GetString());
            Assert.Equal(2, entry.GetProperty("users").GetArrayLength());
            Assert.DoesNotContain("9007199254740993", entry.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", entry.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(2, module.Reads); // metadata GET performs no protocol operation
        }
        Assert.Equal(2, await wire.WaitAsync(TimeSpan.FromSeconds(10), Token)); // stream closed after the last lease, not after the first input
        Assert.Equal(1, module!.SessionDisposals);
        Assert.Equal(2, module.UnitDisposals);

        var retry = ServeOnceAsync(sink, 401);
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => Register(services, false, true, value => module = value)))
        {
            var runtime = missing.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, missing.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            Assert.Equal(2, runtime.GetStatus().Units.Count(unit => unit.Id.Kind == "input.testwire" && unit.State == RuntimeUnitState.BuildFailed));
            Assert.Equal(0, module!.Builds);
            await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.Kind == "input.simulator" && unit.Reads?.ReadsCompleted > 0));
            var replay = await retry.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.Contains(frozen, pair => replay.Body.SequenceEqual(pair.Value) && replay.Header.Contains(pair.Key.ToString(), StringComparison.OrdinalIgnoreCase));
            var versions = missing.Services.GetRequiredService<RuntimeVersionStore>();
            Assert.Equal(hash, RuntimeSnapshotDocument.ReadPlan(versions.Load(1)!).ContentHash);
            Assert.Throws<RuntimePlanDocumentException>(() => versions.ReadRetryCheckpoint(1, hash));
            Assert.False(source.Pending());
        }

        async Task<List<(string Header, byte[] Body)>> ReceiveBoth()
        {
            var one = await ServeOnceAsync(sink, 204);
            var two = await ServeOnceAsync(sink, 204);
            return [one, two];
        }
        var replayed = ReceiveBoth();
        await using (var restored = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => Register(services, true, false, value => module = value)))
        {
            var runtime = restored.Services.GetRequiredService<RuntimeHost>();
            var results = await replayed.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.All(frozen, pair => Assert.Contains(results, received => received.Body.SequenceEqual(pair.Value) &&
                received.Header.Contains(pair.Key.ToString(), StringComparison.OrdinalIgnoreCase)));
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 3).Count == 0);
            Assert.Equal(hash, restored.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
            Assert.Equal(2, module!.Builds);
            Assert.False(source.Pending()); // restored fixture deliberately does not collect another sample
        }
    }

    [Fact]
    public async Task NewConnectionModuleUsesTheOriginalOutboundPolicyAndDoesNotConnectToAnUnapprovedLocalPort()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var source = Listen();
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllBytesAsync(seed, WireDocument(source, "http://127.0.0.1:1/"), Token);
        TestWireModule? module = null;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed }, services => Register(services, true, true, value => module = value));
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        await OutputPipelineTests.UntilAsync(() => module?.Denied > 0);
        Assert.False(source.Pending());
        Assert.Equal(0, module!.Reads);
        Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewConnectionModuleCannotBypassThisHostsAdmissionUsingAnotherDirectoryOrHistoricalRead(bool historical)
    {
        using var data = TemporaryDirectory.Create();
        using var source = Listen();
        TestWireModule? module = null;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            configureServices: services => Register(services, false, true, value => module = value));
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        var pointers = versions.ReadPointers();
        var bytes = WireDocument(source, "http://localhost/");
        var document = historical ? RuntimePlanDocument.ParseStored(bytes) : RuntimePlanDocument.Parse(bytes,
            inputs: new InputModuleCatalog([module!], new([TestWireModule.Definition])));
        Assert.Throws<RuntimePlanDocumentException>(() => versions.Freeze(document, RuntimeSnapshotOrigins.Seed, null, "fixture"));
        Assert.Equal(pointers, versions.ReadPointers());
        Assert.Null(versions.Load(1));
        Assert.Equal(0, module!.Builds);
        Assert.False(source.Pending());
    }

    private static void Register(IServiceCollection services, bool definition, bool emit, Action<TestWireModule> created)
    {
        if (definition)
        { services.AddSingleton(TestWireModule.Definition); }
        services.AddSingleton<IInputModuleFactory>(provider =>
        {
            var module = new TestWireModule(provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<IOutboundConnector>(), emit);
            created(module);
            return module;
        });
    }

    private static byte[] WireDocument(TcpListener source, string target)
    {
        var connection = new ConnectionModuleCatalog([TestWireModule.Definition]).Freeze(10, "testwire", 1,
            JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = ((IPEndPoint)source.LocalEndpoint).Port }));
        object Input(string key) => new
        {
            kind = "input.testwire",
            key,
            configuration = new
            {
                inputModule = new
                {
                    version = 1,
                    parameters = new { source = "10" },
                    connections = new
                    {
                        source = new
                        {
                            connectionId = connection.ConnectionId,
                            kind = connection.Kind,
                            configuration = connection.Configuration
                        }
                    }
                }
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new object[]
        {
            Input("meter/read"), Input("second/read"),
            new { kind = "input.simulator", key = "healthy/read", configuration = new { periodMs = 1000 } },
            new { kind = "output.http", key = "sink", configuration = new { connectionId = 1, baseAddress = target, path = "data", inputs = WireInputs } }
        }
        });
    }

    private static async Task<int> ServeWireAsync(TcpListener listener)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = new byte[1];
        var reply = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(reply, 9007199254740993);
        for (var index = 0; index < 2; index++)
        {
            await stream.ReadExactlyAsync(request, timeout.Token);
            Assert.Equal(42, request[0]);
            await stream.WriteAsync(reply, timeout.Token);
        }
        Assert.Equal(0, await stream.ReadAsync(request, timeout.Token));
        return 2;
    }
}

/// <summary>仅测试侧的8字节响应协议，不添加生产协议。真实TCP必须经原出站检查和共享会话。</summary>
internal sealed class TestWireModule(ISharedConnections shared, IOutboundConnector outbound, bool emit) : IInputModuleFactory
{
    public static ConnectionModuleDefinition Definition { get; } = new("testwire", 1, "Test wire", new("connection.testwire", ParameterScope.Connection,
        [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 255 },
         new("port", "Port", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "1", MaximumJson = "65535" }]));
    private int _builds, _sessions, _reads, _denied, _unitDisposals, _sessionDisposals;
    public int Builds => Volatile.Read(ref _builds);
    public int Sessions => Volatile.Read(ref _sessions);
    public int Reads => Volatile.Read(ref _reads);
    public int Denied => Volatile.Read(ref _denied);
    public int UnitDisposals => Volatile.Read(ref _unitDisposals);
    public int SessionDisposals => Volatile.Read(ref _sessionDisposals);
    public string Kind => "input.testwire";
    public int Version => 1;
    public ParameterSchema Parameters { get; } = new("input.testwire", ParameterScope.Input,
        [new("source", "Source", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["testwire"] }]);
    public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, null)];
    public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
    {
        Interlocked.Increment(ref _builds);
        var connection = context.Connections["source"];
        var parameters = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
        var lease = shared.Acquire(connection.ConnectionId, connection.Kind, ModuleConnectionSnapshot.ModuleConfigurationIdentity(connection), () =>
            new Session(this, outbound, parameters.GetProperty("host").GetString()!, parameters.GetProperty("port").GetInt32(), context.Time));
        return new Unit(this, plan.Id, context, lease, emit);
    }

    private sealed class Unit(TestWireModule owner, RuntimeUnitId id, InputUnitContext context, IConnectionLease<Session> lease, bool emit) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await context.WaitForActivationAsync(stoppingToken);
            if (emit)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), context.Time);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stoppingToken);
                await context.Messages.ProduceAsync(4096, async token =>
                {
                    var value = await lease.ExecuteAsync((session, ct) => session.ReadAsync(ct), token);
                    var at = context.Time.GetUtcNow();
                    Interlocked.Increment(ref owner._reads);
                    context.Reads.ReadCompleted(at, 0);
                    return new EdgeMessage(Guid.CreateVersion7(at), context.InputKey, context.DeviceKey, at, context.GatewayTimeSource, at,
                        [new("value", FieldValue.FromInt64(value))]);
                }, cancellation.Token);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        public async ValueTask DisposeAsync() { await lease.DisposeAsync(); Interlocked.Increment(ref owner._unitDisposals); }
    }

    private sealed class Session : IAsyncDisposable, IConnectionObservationSource
    {
        private readonly TestWireModule _owner;
        private readonly IOutboundConnector _outbound;
        private readonly string _host;
        private readonly int _port;
        private readonly TimeProvider _time;
        private Stream? _stream;
        private ConnectionObservation _observation = new(ConnectionCommunicationState.Connecting);
        public ConnectionObservation Observation => Volatile.Read(ref _observation);
        public Session(TestWireModule owner, IOutboundConnector outbound, string host, int port, TimeProvider time)
        {
            _owner = owner;
            _outbound = outbound;
            _host = host;
            _port = port;
            _time = time;
            Interlocked.Increment(ref owner._sessions);
        }
        public async Task<long> ReadAsync(CancellationToken token)
        {
            try
            {
                _stream ??= await _outbound.ConnectAsync(_host, _port, token);
                await _stream.WriteAsync(new byte[] { 42 }, token);
                var bytes = new byte[8];
                await _stream.ReadExactlyAsync(bytes, token);
                Volatile.Write(ref _observation, new(ConnectionCommunicationState.Connected, _time.GetUtcNow()));
                return BinaryPrimitives.ReadInt64BigEndian(bytes);
            }
            catch (OutboundAccessDeniedException)
            {
                Interlocked.Increment(ref _owner._denied);
                Volatile.Write(ref _observation, new(ConnectionCommunicationState.Degraded, LastErrorCode: "test.denied", LastErrorAt: _time.GetUtcNow()));
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_stream is not null)
            { await _stream.DisposeAsync(); }
            Interlocked.Increment(ref _owner._sessionDisposals);
        }
    }
}
