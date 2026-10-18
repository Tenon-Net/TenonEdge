using System.Collections.Concurrent;
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
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputModuleConnectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModuleAndBuiltinHttpShareOriginalConnectionCredentialAndQueueAfterFrozenRestart()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var address = $"http://127.0.0.1:{port}/api/";
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        long id;
        string draftVersion;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path, settings))
        {
            var saved = setup.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new("0", "shared", "Shared", ConnectionKind.Http, Http: new(address)), "test", null);
            id = saved.Connection.Id;
            draftVersion = saved.Version;
            await setup.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(id,
                new(address, HttpCredentialKind.Bearer, Secret: "fixture-secret", ExpectedDraftVersion: draftVersion), "test", null, true, Token);
        }
        var seed = Path.Combine(seeds.Path, "input-reference.json");
        var moduleInput = Unit(id, address);
        var builtin = new RuntimeUnitPlan(RuntimeUnitId.Create("input.http", "builtin/source"), JsonSerializer.SerializeToElement(new
        { connectionId = id, baseAddress = address, path = "builtin", periodMs = 60000, fields = Fields }));
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "sink"), JsonSerializer.SerializeToElement(new
        { connectionId = id, baseAddress = address, path = "result", inputs = Sources }));
        await File.WriteAllBytesAsync(seed, Document(moduleInput, builtin, output), Token);
        string? hash = null;
        for (var phase = 0; phase < 2; phase++)
        {
            var requests = ReadSharedRequests(listener);
            HttpReferenceModule? module = null;
            var config = new Dictionary<string, string>(settings);
            if (phase == 0)
            { config[RuntimeSettings.SeedSnapshotKey] = seed; }
            await using var host = new FixedDataDirectoryApplicationFactory(data.Path, config, services => services.AddSingleton<IInputModuleFactory>(provider =>
                module = new(provider.GetServices<IInputUnitFactory>().OfType<HttpInputFactory>().Single())));
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var observed = await requests.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.Equal(4, observed.Count);
            Assert.Contains(observed, value => value.Header.StartsWith("GET /api/module ", StringComparison.Ordinal));
            Assert.Contains("X-Module: frozen", Assert.Single(observed, value => value.Header.StartsWith("GET /api/module ", StringComparison.Ordinal)).Header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(observed, value => value.Header.StartsWith("GET /api/builtin ", StringComparison.Ordinal));
            var posts = observed.Where(value => value.Header.StartsWith("POST /api/result ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, posts.Length);
            Assert.All(observed, value => Assert.Contains("Authorization: Bearer " + (phase == 0 ? "fixture-secret" : "rotated-fixture-secret"), value.Header, StringComparison.Ordinal));
            Assert.All(posts, value =>
            {
                using var body = JsonDocument.Parse(value.Body);
                Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
                Assert.Contains("Idempotency-Key:", value.Header, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("fixture-secret", value.Body, StringComparison.Ordinal);
            });
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 2);
            Assert.False(listener.Pending()); // All four requests used the one accepted TCP connection.
            Assert.NotNull(module);
            var context = Assert.Single(module.Contexts);
            Assert.Equal(address, context.Connections["connection"].Configuration.GetProperty("baseAddress").GetString());
            Assert.Equal(id, context.Connections["connection"].ConnectionId);
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.DoesNotContain("fixture-secret", snapshot.Content, StringComparison.Ordinal);
            if (phase == 0)
            {
                hash = snapshot.ContentHash;
                var credentials = host.Services.GetRequiredService<HttpCredentialStore>();
                await credentials.WriteAsync(id, new(address, HttpCredentialKind.Bearer, Secret: "rotated-fixture-secret",
                    ExpectedVersion: credentials.Read(id)!.Version, AccountChanged: false, ExpectedDraftVersion: draftVersion), "test", null, true, Token);
                // A saved, unapplied connection change must not alter the frozen context on restart.
                host.Services.GetRequiredService<ConnectionDraftStore>().Write(id,
                    new(draftVersion, "shared", "Changed draft", ConnectionKind.Http, Http: new("http://unapplied.invalid/")), "test", null);
            }
            else
            { Assert.Equal(hash, snapshot.ContentHash); }
        }
    }

    [Fact]
    public async Task ModuleReferenceReservedAfterImportPreviewRejectsTheWholeCommit()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, inert: true);
        TestAccounts.Create(host.Services, "importer", ApiRequests.StrongPassword, UserRole.Configurator);
        var user = TestAccounts.Find(host.Services, "importer")!;
        var actor = new ManagementActor(user.Id, user.SecurityStamp, null);
        var store = host.Services.GetRequiredService<ConfigurationImportStore>();
        var file = new PortableConfiguration(1, [new("new-source", "Source", ConnectionKind.Http, true,
            null, null, new("http://localhost/"), null)], [], [], []);
        var candidate = store.Prepare(ConfigurationExport.Serialize(file, Token), ConfigurationImportMode.Merge, "0", Token);
        var id = Assert.Single(candidate.NewConnectionIds);
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Freeze(RuntimePlanDocument.Parse(Document(Unit(id, "http://localhost/")), inputs: host.Services.GetRequiredService<InputModuleCatalog>()),
            RuntimeSnapshotOrigins.Seed, null, "test");
        var database = host.Services.GetRequiredService<ConfigDatabase>();
        var before = database.InTransaction(client => ConfigurationCheckpoint.Read(client, "0"));
        Assert.Equal("connection.reservedIdentity", Assert.Throws<DraftWriteException>(() => store.Commit(candidate, actor, Token)).Code);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(database.InTransaction(client => ConfigurationCheckpoint.Read(client, "0"))));
    }

    [Fact]
    public async Task ModuleOnlyRetainedReferencesProtectDeletionCredentialsAndInvalidateChangedRecipients()
    {
        using var data = TemporaryDirectory.Create();
        long id;
        string version;
        string credentialVersion;
        string hash;
        await using (var host = Host(data.Path, inert: true))
        {
            var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
            var saved = connections.Write(null, new("0", "source", "Source", ConnectionKind.Http, Http: new("http://localhost/")), "test", null);
            id = saved.Connection.Id;
            version = saved.Version;
            var credentials = host.Services.GetRequiredService<HttpCredentialStore>();
            var credential = await credentials.WriteAsync(id,
                new("http://localhost/", HttpCredentialKind.Bearer, Secret: "fixture-secret", ExpectedDraftVersion: version), "test", null, true, Token);
            credentialVersion = credential.Version;
            var store = host.Services.GetRequiredService<RuntimeVersionStore>();
            var plan = RuntimePlanDocument.Parse(Document(Unit(id, "http://localhost/")), inputs: host.Services.GetRequiredService<InputModuleCatalog>());
            var revision = store.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "test");
            Assert.Equal("connection.referenced", Assert.Throws<DraftWriteException>(() => connections.Delete(id, version, "test", null)).Code);
            Assert.Equal("credential.connectionReferenced", Assert.Throws<CredentialWriteException>(() => credentials.Clear(id, version, credentialVersion, "test", null)).Code);
            store.Commit(revision, null);
            hash = store.Load(revision)!.ContentHash;
        }
        // Historical reference protection cannot depend on the extension still being installed.
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            var runtime = missing.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeUnitState.BuildFailed, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "input.testreference").State);
            var connections = missing.Services.GetRequiredService<ConnectionDraftStore>();
            var credentials = missing.Services.GetRequiredService<HttpCredentialStore>();
            Assert.Equal("connection.referenced", Assert.Throws<DraftWriteException>(() => connections.Delete(id, version, "test", null)).Code);
            Assert.Equal("credential.connectionReferenced", Assert.Throws<CredentialWriteException>(() => credentials.Clear(id, version, credentialVersion, "test", null)).Code);
            Assert.Equal(hash, missing.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        }
        await using var restored = Host(data.Path, inert: true);
        var changed = RuntimePlanDocument.Parse(Document(Unit(id, "http://changed.invalid/")), inputs: restored.Services.GetRequiredService<InputModuleCatalog>());
        var versions = restored.Services.GetRequiredService<RuntimeVersionStore>();
        var next = versions.Freeze(changed, RuntimeSnapshotOrigins.Seed, null, "test");
        versions.Commit(next, null);
        Assert.False(restored.Services.GetRequiredService<HttpCredentialStore>().Read(id)!.Configured);
        Assert.Null(restored.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(id, "http://changed.invalid/"));
    }

    [Fact]
    public async Task SupervisorRebuildKeepsTheSameContextAndMalformedConnectionOnlyFailsItsOwnInput()
    {
        using var data = TemporaryDirectory.Create();
        HttpReferenceModule? module = null;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => services.AddSingleton<IInputModuleFactory>(provider =>
            module = new(provider.GetServices<IInputUnitFactory>().OfType<HttpInputFactory>().Single()) { Inert = true, FailFirst = true }));
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var good = Unit(11, "http://localhost/");
        var bad = new RuntimeUnitPlan(RuntimeUnitId.Create("input.testreference", "bad/source"), JsonSerializer.Deserialize<JsonElement>("""
            {"inputModule":{"version":1,"parameters":{"connection":"12"},"connections":{"connection":null}}}
            """));
        await using var prepared = runtime.Prepare(new(1, [good, bad]));
        Assert.Equal(bad.Id, Assert.Single(prepared.Failures).Id);
        await runtime.ActivateAsync(prepared);
        Assert.NotNull(module);
        await OutputPipelineTests.UntilAsync(() => module.Contexts.Count == 2);
        Assert.Same(module.Contexts.ToArray()[0], module.Contexts.ToArray()[1]);
        Assert.Equal(11, module.Contexts.First().Connections["connection"].ConnectionId);
        Assert.Equal(1, module.Disposed);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(runtime.GetStatus()), StringComparison.Ordinal);
    }

    private static FixedDataDirectoryApplicationFactory Host(string data, bool inert) => new(data,
        configureServices: services => services.AddSingleton<IInputModuleFactory>(provider =>
            new HttpReferenceModule(provider.GetServices<IInputUnitFactory>().OfType<HttpInputFactory>().Single()) { Inert = inert }));
    private static readonly string[] Sources = ["meter/source", "builtin/source"];
    private static readonly object[] Fields = [new { key = "value", path = "/value", type = "integer" }];
    private static RuntimeUnitPlan Unit(long id, string address) => new(RuntimeUnitId.Create("input.testreference", "meter/source"),
        JsonSerializer.SerializeToElement(new
        {
            inputModule = new
            {
                version = 1,
                parameters = new { connection = id.ToString(CultureInfo.InvariantCulture) },
                connections = new { connection = new { connectionId = id, kind = "http", configuration = new { baseAddress = address, timeoutMs = 5000, headers = new Dictionary<string, string> { ["X-Module"] = "frozen" } } } },
            },
        }));
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) });

    private static async Task<List<(string Header, string Body)>> ReadSharedRequests(TcpListener listener)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var result = new List<(string Header, string Body)>();
        for (var index = 0; index < 4; index++)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 32768)
            {
                await stream.ReadExactlyAsync(one, timeout.Token);
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                { break; }
            }
            var header = Encoding.UTF8.GetString(bytes.ToArray());
            var length = header.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var body = new byte[length is null ? 0 : int.Parse(length.Split(':')[1], CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(body, timeout.Token);
            result.Add((header, Encoding.UTF8.GetString(body)));
            var json = header.StartsWith("GET ", StringComparison.Ordinal) ? "{\"value\":9007199254740993}" : "";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\n\r\n{json}"), timeout.Token);
        }
        return result;
    }

    private sealed class HttpReferenceModule(HttpInputFactory original) : IInputModuleFactory
    {
        private int _builds;
        private int _disposed;
        public string Kind => "input.testreference";
        public int Version => 1;
        public bool Inert { get; init; }
        public bool FailFirst { get; init; }
        public int Disposed => Volatile.Read(ref _disposed);
        public ConcurrentQueue<InputUnitContext> Contexts { get; } = new();
        public ParameterSchema Parameters { get; } = new("input.testreference", ParameterScope.Input,
            [new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, "count")];
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            var connection = context.Connections["connection"];
            Assert.Equal(connection.ConnectionId.ToString(CultureInfo.InvariantCulture), unit.Configuration.GetProperty("connection").GetString());
            Contexts.Enqueue(context);
            var number = Interlocked.Increment(ref _builds);
            if (Inert)
            { return new Idle(this, unit.Id, context, FailFirst && number == 1); }
            var http = connection.Configuration.Deserialize<HttpConnectionDraft>(JsonSerializerOptions.Web)!;
            return original.Build(new(unit.Id, JsonSerializer.SerializeToElement(new
            { connectionId = connection.ConnectionId, baseAddress = http.BaseAddress, timeoutMs = http.TimeoutMs, headers = http.Headers, path = "module", periodMs = 60000, fields = Fields })), context);
        }
        private sealed class Idle(HttpReferenceModule owner, RuntimeUnitId id, InputUnitContext context, bool fail) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                await context.WaitForActivationAsync(stoppingToken);
                if (fail)
                { throw new InvalidOperationException("fixture-secret"); }
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            public ValueTask DisposeAsync()
            { Interlocked.Increment(ref owner._disposed); return ValueTask.CompletedTask; }
        }
    }
}
