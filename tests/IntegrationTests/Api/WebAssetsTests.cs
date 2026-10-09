using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Api;

public sealed class WebAssetsTests
{
    private const string Html = "<!doctype html><title>SPA test fixture, not product UI</title>";

    [Theory]
    [InlineData("/")]
    [InlineData("/devices/123/points")]
    [InlineData("/routes/editor?draft=123")]
    [InlineData("/index.html")]
    public async Task DocumentNavigationServesPublicShellWithoutSession(string path)
    {
        using var directory = CreateAssets();
        using var provider = new PhysicalFileProvider(directory.Path);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, path, "text/html");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Html, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/api/missing")]
    [InlineData("/API/edge-admin/v1/missing")]
    [InlineData("/api/edge-admin/v1/unknown.json")]
    [InlineData("/assets/missing.js")]
    [InlineData("/assets/missing")]
    [InlineData("/missing.css")]
    [InlineData("/healthz/missing")]
    [InlineData("/readyz/missing")]
    public async Task MissingApiAndAssetsRemainProblemDetails(string path)
    {
        using var directory = CreateAssets();
        using var provider = new PhysicalFileProvider(directory.Path);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, path, "text/html");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("POST", "/devices/123", "text/html", HttpStatusCode.NotFound)]
    [InlineData("GET", "/devices/123", "application/json", HttpStatusCode.NotFound)]
    [InlineData("GET", "/devices/123", "text/html;q=0", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/api/edge-admin/v1/auth/session", "text/html", HttpStatusCode.MethodNotAllowed)]
    [InlineData("GET", "/api/edge-admin/v1/auth/session", "text/html", HttpStatusCode.Unauthorized)]
    public async Task FallbackDoesNotChangeRequestOrAuthorizationSemantics(
        string method, string path, string accept, HttpStatusCode status)
    {
        using var directory = CreateAssets();
        using var provider = new PhysicalFileProvider(directory.Path);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var request = Request(new HttpMethod(method), path, accept);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await ProblemAssert.HasStatusAsync(response, status, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StaticAssetsSupportConditionalRequestsAndHeadHasNoBody()
    {
        using var directory = CreateAssets();
        using var provider = new PhysicalFileProvider(directory.Path);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/assets/app.js", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/* fixture */", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(response.Headers.ETag);
        using var conditional = Request(HttpMethod.Get, "/assets/app.js", "*/*");
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var unchanged = await client.SendAsync(conditional, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        using var head = Request(HttpMethod.Head, "/devices/123", "text/html");
        using var headResponse = await client.SendAsync(head, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(string.Empty, await headResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingFrontendBuildDoesNotInventAWorkingPage()
    {
        using var directory = TemporaryDirectory.Create();
        using var provider = new PhysicalFileProvider(directory.Path);
        await using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, "/", "text/html");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await ProblemAssert.HasStatusAsync(response, HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
    }

    private static AssetsFactory CreateFactory(IFileProvider provider) => new(provider);

    private sealed class AssetsFactory(IFileProvider provider) : TenonEdgeApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices((context, _) => context.HostingEnvironment.WebRootFileProvider = provider);
        }
    }

    private static TemporaryDirectory CreateAssets()
    {
        var directory = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(directory.Path, "assets"));
        File.WriteAllText(Path.Combine(directory.Path, "index.html"), Html);
        File.WriteAllText(Path.Combine(directory.Path, "assets", "app.js"), "/* fixture */");
        // Even accidental files under API paths must not become public static responses.
        Directory.CreateDirectory(Path.Combine(directory.Path, "api", "edge-admin", "v1"));
        File.WriteAllText(Path.Combine(directory.Path, "api", "edge-admin", "v1", "unknown.json"), "secret fixture");
        return directory;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string accept)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Accept.ParseAdd(accept);
        return request;
    }
}
