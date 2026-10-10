using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeOperationEndpointTests
{
    private const string Path = "/api/edge-admin/v1/configuration/operations";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllRolesCanReadRealOperationMetadataButAnonymousCannot()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        await coordinator.ApplyDraftAsync("0", "operator", "127.0.0.1", Token);
        var operation = coordinator.ApplicationStatus!;
        using var anonymous = factory.CreateClient();
        using (var response = await anonymous.GetAsync(new Uri(Path, UriKind.Relative), Token))
        { await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, Token); }
        using (var response = await anonymous.GetAsync(new Uri($"{Path}/{operation.Id}", UriKind.Relative), Token))
        { await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, Token); }
        foreach (var account in new[] { "admin", "operator", "viewer" })
        {
            using var client = await factory.CreateLoggedInClientAsync(account, Token);
            var list = await client.GetJsonAsync(Path, Token);
            Assert.Equal(1, list.GetArrayLength());
            Assert.Equal(operation.Id, list[0].GetProperty("id").GetGuid());
            var detail = await client.GetJsonAsync($"{Path}/{operation.Id}", Token);
            Assert.Equal("operator", detail.GetProperty("account").GetString());
            Assert.Equal("completed", detail.GetProperty("stage").GetString());
            Assert.True(detail.GetProperty("committed").GetBoolean());
            Assert.Equal(1, detail.GetProperty("result").GetProperty("revision").GetInt64());
            Assert.False(detail.TryGetProperty("sourceIp", out _));
            Assert.False(detail.TryGetProperty("exception", out _));
        }
    }

    [Fact]
    public async Task UnknownOperationIsExplicitlyNotFoundAndMetadataIsNotCached()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("viewer", Token);
        using (var response = await client.GetAsync(new Uri(Path, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        using (var response = await client.GetAsync(new Uri($"{Path}/{Guid.NewGuid()}", UriKind.Relative), Token))
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.NotFound, Token);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
    }
}
