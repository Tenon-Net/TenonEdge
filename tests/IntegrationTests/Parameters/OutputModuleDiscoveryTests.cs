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

    private sealed class Declaration(string kind, int version) : IOutputModuleFactory
    {
        public string Kind => kind;
        public int Version => version;
        public string DisplayName => "测试输出声明";
        public int Calls { get; private set; }
        public ParameterSchema Parameters { get; } = new("output.discovery", ParameterScope.Output,
        [
            new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
            new("number", "精确默认值", ParameterType.Number) { DefaultJson = "9007199254740993", WholeNumber = true },
        ]);
        public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
        {
            Calls++;
            throw new InvalidOperationException("发现声明时不能执行绑定或发送。");
        }
    }
}
