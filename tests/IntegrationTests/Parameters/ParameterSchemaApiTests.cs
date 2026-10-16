using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Parameters;

public sealed class ParameterSchemaApiTests
{
    private static readonly string[] ParameterTypes = ["text", "number", "switch", "choice", "credential", "connectionReference"];
    private const string Endpoint = "/api/edge-admin/v1/parameter-schemas/";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task AuthenticatedRolesReadOriginalDeclarationsWithoutMutatingDrafts(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var before = store.Read();
        using var response = await client.GetAsync(new Uri(Endpoint + "connection.modbus", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.ReadJsonAsync(Token);
        Assert.Equal("connection.modbus", body.GetProperty("id").GetString());
        Assert.Equal("connection", body.GetProperty("scope").GetString());
        var parameters = body.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(7, parameters.Length);
        var timeout = parameters.Single(p => p.GetProperty("name").GetString() == "readTimeoutMs");
        Assert.Equal("number", timeout.GetProperty("type").GetString());
        Assert.Equal("100", timeout.GetProperty("minimumJson").GetString());
        Assert.Equal("60000", timeout.GetProperty("maximumJson").GetString());
        Assert.Equal(before.Version, store.Read().Version);
        Assert.Empty(store.Read().Connections);
    }

    [Fact]
    public async Task MissingSchemaIs404AndAnonymousIs401NotAFormFallback()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Endpoint + "connection.modbus", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", Token);
        using var missing = await viewer.GetAsync(new Uri(Endpoint + "not-installed", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
        Assert.Equal("parameters.missing", (await missing.ReadJsonAsync(Token)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExplicitRegistrationPublishesAllSixTypesAndKeepsExactDefaultText()
    {
        await using var original = new TenonEdgeApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(
            new ParameterSchema("connection.example", ParameterScope.Connection,
            [
                new("text", "文本", ParameterType.Text) { Required = true, MaximumLength = 64, Group = "基本", Description = "普通文字" },
                new("number", "数字", ParameterType.Number) { DefaultJson = "9007199254740993", WholeNumber = true },
                new("enabled", "开关", ParameterType.Switch) { DefaultJson = "false" },
                new("mode", "选择", ParameterType.Choice) { Options = [new("one", "一")] },
                new("password", "凭证", ParameterType.Credential),
                new("connection", "连接", ParameterType.ConnectionReference) { ConnectionKinds = ["mqtt"] },
            ]))));
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", Token);
        using var response = await client.GetAsync(new Uri(Endpoint + "connection.example", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parameters = (await response.ReadJsonAsync(Token)).GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(ParameterTypes,
            parameters.Select(p => p.GetProperty("type").GetString()));
        Assert.Equal("9007199254740993", parameters[1].GetProperty("defaultJson").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, parameters[4].GetProperty("defaultJson").ValueKind);
    }
}
