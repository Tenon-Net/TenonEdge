using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Parameters;

public sealed class OutputModuleDiscoveryTests
{
    private const string Endpoint = "/api/edge-admin/v1/output-modules";
    private static readonly string[] ExpectedKinds = ["output.discoverya", "output.discoverya", "output.discoveryb"];
    private static readonly int[] ExpectedVersions = [1, 2, 1];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", true, true)]
    [InlineData("Configurator", true, true)]
    [InlineData("Administrator", true, true)]
    [InlineData("Administrator", false, true)]
    [InlineData("Administrator", true, false)]
    public async Task DiscoveryListsOnlyDeclaredInstalledTransportsIncludingPlanScopedMqttWithoutBinding(string role, bool connectionInstalled, bool transportInstalled)
    {
        using var data = TemporaryDirectory.Create();
        var wire = new TestWireOutputModule { Throw = true };
        var http = new TestProtocolOutputModule("http") { Throw = true };
        var mqtt = new TestProtocolOutputModule("mqtt") { Throw = true };
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton<IOutputModuleFactory>(wire);
            services.AddSingleton<IOutputModuleFactory>(http);
            services.AddSingleton<IOutputModuleFactory>(mqtt);
            if (connectionInstalled)
            { services.AddSingleton(TestWireOutputModule.Definition); }
            if (transportInstalled)
            {
                services.AddSingleton<TestWireOutputTransport>();
                services.AddSingleton<IOutputAdapterFactory>(provider => provider.GetRequiredService<TestWireOutputTransport>());
            }
        });
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var modules = (await response.ReadJsonAsync(Token)).EnumerateArray().ToDictionary(value => value.GetProperty("kind").GetString()!);
        Assert.Equal("http", Assert.Single(modules[http.Kind].GetProperty("targetConnectionKinds").EnumerateArray()).GetString());
        Assert.Equal("mqtt", Assert.Single(modules[mqtt.Kind].GetProperty("targetConnectionKinds").EnumerateArray()).GetString());
        var kinds = modules[wire.Kind].GetProperty("targetConnectionKinds").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(connectionInstalled && transportInstalled ? ["wire"] : Array.Empty<string>(), kinds);
        Assert.Equal(0, wire.Calls);
        Assert.Equal(0, http.Calls);
        Assert.Equal(0, mqtt.Calls);
        if (transportInstalled)
        {
            var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
            Assert.Equal(0, transport.QueueBindings);
            Assert.Equal(0, transport.Adapters);
            Assert.Equal(0, transport.Sessions);
        }
    }

    [Fact]
    public async Task PlanScopedDiscoveryDeduplicatesAndSortsWithoutCreatingAnotherFactorySet()
    {
        using var data = TemporaryDirectory.Create();
        var declaration = new Declaration("output.discovery", 1, ["wire", "mqtt", "http", "absent"]);
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
            services.AddSingleton<DiscoveryProvider>()
                .AddSingleton<IRuntimePlanAdapterProvider>(services => services.GetRequiredService<DiscoveryProvider>())
                .AddSingleton(TestWireOutputModule.Definition)
                .AddSingleton<IOutputModuleFactory>(declaration));
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var provider = host.Services.GetRequiredService<DiscoveryProvider>();
        var created = provider.Creates;
        using var response = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var module = Assert.Single((await response.ReadJsonAsync(Token)).EnumerateArray());
        Assert.Equal(["http", "mqtt", "wire"], module.GetProperty("targetConnectionKinds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(created, provider.Creates);
        Assert.Equal(0, declaration.Calls);
    }

    private sealed class DiscoveryProvider(ISharedConnections connections, IOutboundConnector outbound) : IRuntimePlanAdapterProvider
    {
        public int Creates { get; private set; }
        public IReadOnlyList<string> OutputKinds => ["output.wire", "output.wire", "output.absent", "input.fake"];
        public RuntimePlanAdapters Create()
        {
            Creates++;
            return new([], [new TestWireOutputTransport(connections, outbound)]);
        }
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task AuthenticatedDiscoveryReturnsExactDeclarationsWithoutBindingOrChangingDrafts(string role)
    {
        using var data = TemporaryDirectory.Create();
        Declaration[] declarations = [new("output.discoveryb", 1), new("output.discoverya", 2), new("output.discoverya", 1)];
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            foreach (var declaration in declarations)
            { services.AddSingleton<IOutputModuleFactory>(declaration); }
        });
        using var anonymous = host.CreateClient();
        using var denied = await anonymous.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var drafts = host.Services.GetRequiredService<ConnectionDraftStore>();
        var before = drafts.Read();
        using var response = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var modules = (await response.ReadJsonAsync(Token)).EnumerateArray().ToArray();
        Assert.Equal(ExpectedKinds, modules.Select(module => module.GetProperty("kind").GetString()));
        Assert.Equal(ExpectedVersions, modules.Select(module => module.GetProperty("version").GetInt32()));
        foreach (var module in modules)
        {
            Assert.Equal("测试输出声明", module.GetProperty("displayName").GetString());
            var schema = module.GetProperty("parameters");
            Assert.Equal("output", schema.GetProperty("scope").GetString());
            var fields = schema.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.Equal(2, fields.Length);
            Assert.Equal("connectionReference", fields[0].GetProperty("type").GetString());
            Assert.True(fields[0].GetProperty("required").GetBoolean());
            Assert.Equal("http", Assert.Single(fields[0].GetProperty("connectionKinds").EnumerateArray()).GetString());
            Assert.Equal("number", fields[1].GetProperty("type").GetString());
            Assert.Equal("9007199254740993", fields[1].GetProperty("defaultJson").GetString());
        }
        Assert.All(declarations, declaration => Assert.Equal(0, declaration.Calls));
        Assert.Equal(before.Version, drafts.Read().Version);
        Assert.Empty(drafts.Read().Connections);
        Assert.Empty(host.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
    }

    [Fact]
    public async Task DefaultHostPublishesAnEmptyCatalogRatherThanDemoOutputs()
    {
        await using var host = new TenonEdgeApplicationFactory();
        TestAccounts.Create(host.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await host.CreateLoggedInClientAsync("viewer", Token);
        using var response = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Empty((await response.ReadJsonAsync(Token)).EnumerateArray());
    }

    private sealed class Declaration(string kind, int version, string[]? connectionKinds = null) : IOutputModuleFactory
    {
        public string Kind => kind;
        public int Version => version;
        public string DisplayName => "测试输出声明";
        public int Calls { get; private set; }
        public ParameterSchema Parameters { get; } = new("output.discovery", ParameterScope.Output,
        [
            new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = connectionKinds is null ? ["http"] : [.. connectionKinds] },
            new("number", "精确默认值", ParameterType.Number) { DefaultJson = "9007199254740993", WholeNumber = true },
        ]);
        public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
        {
            Calls++;
            throw new InvalidOperationException("发现声明时不能执行绑定或发送。");
        }
    }
}
