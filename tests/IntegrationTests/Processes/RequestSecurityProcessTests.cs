using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 请求安全的真实网络验证（T0-08；V-11 T0 子集、AT-32 最小版本中的转发头与 Cookie 部分）：
/// <list type="bullet">
/// <item>没有登记受信任代理时，伪造的转发头不改变协议判定与来源地址；设置 ASPNETCORE_FORWARDEDHEADERS_ENABLED 也不改变。</item>
/// <item>登记代理后只接受代理的转发头：协议只看 X-Forwarded-Proto，没有该头时按 HTTP 处理（即使到达的是 HTTPS 端口）。</item>
/// <item>登录限流按实际来源地址计数；来自 127.0.0.2 的连接用于模拟另一台客户端。</item>
/// <item>真实 TLS 下 HTTPS 会话使用 __Host- 前缀、带 Secure 的 Cookie，两种协议的会话不能互换。</item>
/// </list>
/// </summary>
public sealed class RequestSecurityProcessTests(ITestOutputHelper output)
{
    private const string ForgedClient = "203.0.113.9";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgedForwardedHeadersAreIgnoredWithoutRegisteredProxies(bool frameworkVariableSet)
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        var environment = frameworkVariableSet
            ? new Dictionary<string, string> { ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true" }
            : null;

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path, environmentVariables: environment);
        try
        {
            var (http, _) = await StartAsync(host, cancellationToken);
            using var client = CreateClient(http);

            using var setup = await PostAsync(client, ApiRequests.Setup, SetupBody(), cancellationToken, forwardedFor: ForgedClient, forwardedProto: "https");
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            AssertHttpSessionCookie(setup);
            if (frameworkVariableSet)
            {
                Assert.Contains("已忽略 ASPNETCORE_FORWARDEDHEADERS_ENABLED", host.Output, StringComparison.Ordinal);
            }

            await StopAsync(host, cancellationToken);
        }
        finally
        {
            output.WriteLine(host.Output);
        }

        using var store = TestConfigStore.Open(dataDirectory.Path);
        store.Initialize();
        Assert.Equal("127.0.0.1", store.Database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId).SetupSourceIp);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task RegisteredProxyAloneDecidesTheProtocolAndTheClientAddress()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            [$"--{TrustedProxies.ConfigurationKey}:0", "127.0.0.1"]);
        try
        {
            var (http, https) = await StartAsync(host, cancellationToken);

            // 代理经 HTTP 端口转发、声明客户端使用 HTTPS：按 HTTPS 签发 __Host- Cookie，来源地址为代理转发的客户端地址。
            using (var proxied = CreateClient(http))
            using (var setup = await PostAsync(proxied, ApiRequests.Setup, SetupBody(), cancellationToken, forwardedFor: ForgedClient, forwardedProto: "https"))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
                AssertHttpsSessionCookie(setup);
            }

            // 来自代理、没有 X-Forwarded-Proto：即使到达的是 HTTPS 端口，也按 HTTP 处理。
            using (var proxiedOverTls = CreateClient(https))
            using (var login = await PostAsync(proxiedOverTls, ApiRequests.Login, LoginBody(), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                AssertHttpSessionCookie(login);
            }

            // 未登记的来源（127.0.0.2）伪造转发头：全部忽略。
            using (var other = CreateClient(http, IPAddress.Parse("127.0.0.2")))
            using (var login = await PostAsync(other, ApiRequests.Login, LoginBody(), cancellationToken, forwardedFor: ForgedClient, forwardedProto: "https"))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                AssertHttpSessionCookie(login);
            }

            await StopAsync(host, cancellationToken);
        }
        finally
        {
            output.WriteLine(host.Output);
        }

        using var store = TestConfigStore.Open(dataDirectory.Path);
        store.Initialize();
        Assert.Equal(ForgedClient, store.Database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId).SetupSourceIp);
        var logins = store.Database.Client.Queryable<AuditLogRecord>().Where(record => record.Action == AuditActions.Login).OrderBy(record => record.Id).ToList();
        Assert.Equal(["127.0.0.1", "127.0.0.2"], logins.Select(record => record.SourceIp));
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task LoginRateLimitIsCountedPerRealSourceAddress()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            var (http, _) = await StartAsync(host, cancellationToken);
            using var client = CreateClient(http);

            // 每次换一个账号名，避免触发按账号的锁定；每次伪造不同的来源地址，限流仍按实际连接计数。
            for (var attempt = 0; attempt < LoginRateLimiting.PermitLimit; attempt++)
            {
                using var response = await PostAsync(
                    client,
                    ApiRequests.Login,
                    new { accountName = $"user{attempt}", password = "Wrong-Password-1" },
                    cancellationToken,
                    forwardedFor: $"198.51.100.{attempt + 1}");
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var limited = await PostAsync(client, ApiRequests.Login, new { accountName = "user99", password = "Wrong-Password-1" }, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.Equal(LoginRateLimiting.Code, await limited.ReadProblemCodeAsync(cancellationToken));
                Assert.NotNull(limited.Headers.RetryAfter);
            }

            using (var other = CreateClient(http, IPAddress.Parse("127.0.0.2")))
            using (var fromOtherAddress = await PostAsync(other, ApiRequests.Login, new { accountName = "user99", password = "Wrong-Password-1" }, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, fromOtherAddress.StatusCode);
            }

            await StopAsync(host, cancellationToken);
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task RealTlsSessionUsesTheHostPrefixedCookieAndCannotCrossProtocols()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            var (http, https) = await StartAsync(host, cancellationToken);
            using var tlsClient = CreateClient(https);
            using var plainClient = CreateClient(http);

            string httpsValue;
            using (var setup = await PostAsync(tlsClient, ApiRequests.Setup, SetupBody(), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
                httpsValue = AssertHttpsSessionCookie(setup);
            }

            string httpValue;
            using (var login = await PostAsync(plainClient, ApiRequests.Login, LoginBody(), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                httpValue = AssertHttpSessionCookie(login);
            }

            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(tlsClient, SessionAuthentication.HttpsCookieName, httpsValue, cancellationToken));
            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(plainClient, SessionAuthentication.HttpCookieName, httpValue, cancellationToken));
            Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(plainClient, SessionAuthentication.HttpCookieName, httpsValue, cancellationToken));
            Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(tlsClient, SessionAuthentication.HttpsCookieName, httpValue, cancellationToken));

            await StopAsync(host, cancellationToken);
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    private static object SetupBody() => new { accountName = "admin", password = ApiRequests.StrongPassword };

    private static object LoginBody() => new { accountName = "admin", password = ApiRequests.StrongPassword };

    private static async Task<(Uri Http, Uri Https)> StartAsync(HostProcess host, CancellationToken cancellationToken)
    {
        var http = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
        var https = await host.WaitForHttpsAddressAsync(StartupTimeout, cancellationToken);
        using var probe = new HttpClient { BaseAddress = http, Timeout = TimeSpan.FromSeconds(10) };
        await host.WaitUntilReadyAsync(probe, StartupTimeout, cancellationToken);
        return (http, https);
    }

    private static async Task StopAsync(HostProcess host, CancellationToken cancellationToken)
    {
        await host.RequestStopAsync(cancellationToken);
        Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
    }

    /// <summary>不自动处理 Cookie 的客户端；可指定本地源地址，模拟来自另一台机器的连接。自签证书只接受“链不受信任”这一种错误。</summary>
    private static HttpClient CreateClient(Uri baseAddress, IPAddress? localAddress = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.RemoteCertificateChainErrors,
            },
        };
        if (localAddress is not null)
        {
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        object body,
        CancellationToken cancellationToken,
        string? forwardedFor = null,
        string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body, options: JsonSerializerOptions.Web),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return client.SendAsync(request, cancellationToken);
    }

    [Fact]
    public void HttpCookieValueContainingSecureIsNotASecureAttribute()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add(HeaderNames.SetCookie, $"{SessionAuthentication.HttpCookieName}=opaqueSecureValue; path=/; httponly; samesite=strict");
        Assert.Equal("opaqueSecureValue", AssertHttpSessionCookie(response));
    }

    private static string AssertHttpSessionCookie(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie));
        Assert.StartsWith($"{SessionAuthentication.HttpCookieName}=", header, StringComparison.Ordinal);
        Assert.False(SetCookieHeaderValue.Parse(header).Secure);
        return CookieValue(header);
    }

    private static string AssertHttpsSessionCookie(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie));
        Assert.StartsWith($"{SessionAuthentication.HttpsCookieName}=", header, StringComparison.Ordinal);
        var cookie = SetCookieHeaderValue.Parse(header);
        Assert.True(cookie.Secure);
        Assert.Equal("/", cookie.Path.Value);
        Assert.False(cookie.Domain.HasValue);
        return CookieValue(header);
    }

    private static string CookieValue(string header) =>
        header[(header.IndexOf('=', StringComparison.Ordinal) + 1)..header.IndexOf(';', StringComparison.Ordinal)];

    private static async Task<HttpStatusCode> SessionStatusAsync(HttpClient client, string cookieName, string cookieValue, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRequests.Session, UriKind.Relative));
        request.Headers.Add(HeaderNames.Cookie, $"{cookieName}={cookieValue}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Assert.Equal(RequestSecurityPipeline.SessionChallenge, response.Headers.WwwAuthenticate.ToString());
        }

        return response.StatusCode;
    }
}
