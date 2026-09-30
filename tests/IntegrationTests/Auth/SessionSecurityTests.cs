using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

/// <summary>
/// 角色、会话失效与防 CSRF（T0-08；V-11 T0 子集，BE-AT-02）：未登录 401 且带质询头、无权 403；Cookie 写操作校验防 CSRF 令牌；
/// 改密码、改角色、停用、会话代次加一都使旧会话失效；HTTP 与 HTTPS 的会话不能互换。
/// 进程内测试以 TestServer 的地址方案区分协议；真实 TLS 与转发头见 RequestSecurityProcessTests。
/// </summary>
public sealed class SessionSecurityTests
{
    private const string Audit = "/api/edge-admin/v1/audit";

    [Fact]
    public async Task AnonymousGets401WithChallengeAndRolesWithoutPermissionGet403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);

        using (var anonymous = factory.CreateClient())
        using (var response = await anonymous.GetAsync(new Uri(Audit, UriKind.Relative), cancellationToken))
        {
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Unauthorized, cancellationToken);
            Assert.Equal(RequestSecurityPipeline.SessionChallenge, response.Headers.WwwAuthenticate.ToString());
        }

        foreach (var accountName in new[] { "operator", "viewer" })
        {
            using var client = await factory.CreateLoggedInClientAsync(accountName, cancellationToken);
            using var response = await client.GetAsync(new Uri(Audit, UriKind.Relative), cancellationToken);
            await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Forbidden, cancellationToken);
        }

        using var administrator = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        using var allowed = await administrator.GetAsync(new Uri(Audit, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task CookieWritesWithoutTheMatchingCsrfTokenAreRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = factory.CreateClient();
        string csrfToken;
        using (var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken))
        {
            csrfToken = (await login.ReadJsonAsync(cancellationToken)).GetProperty("csrfToken").GetString()!;
        }

        using (var missing = await client.PostAsync(new Uri(ApiRequests.Logout, UriKind.Relative), content: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            Assert.Equal(CsrfProtectionFilter.Code, await missing.ReadProblemCodeAsync(cancellationToken));
        }

        client.DefaultRequestHeaders.Add(CsrfProtectionFilter.HeaderName, csrfToken[..^1] + (csrfToken[^1] == 'A' ? 'B' : 'A'));
        using (var wrong = await client.PostAsync(new Uri(ApiRequests.Logout, UriKind.Relative), content: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        // 读操作不需要令牌。
        client.DefaultRequestHeaders.Remove(CsrfProtectionFilter.HeaderName);
        using (var read = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        client.DefaultRequestHeaders.Add(CsrfProtectionFilter.HeaderName, csrfToken);
        using var accepted = await client.PostAsync(new Uri(ApiRequests.Logout, UriKind.Relative), content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordEndsTheOtherSessionsOfThatAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var first = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        using var second = await factory.CreateLoggedInClientAsync("admin", cancellationToken);

        using (var changed = await first.PostJsonAsync(
            ApiRequests.ChangePassword,
            new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        await AssertSessionStatusAsync(first, HttpStatusCode.OK, cancellationToken);
        await AssertSessionStatusAsync(second, HttpStatusCode.Unauthorized, cancellationToken);
    }

    [Fact]
    public async Task RoleChangeOrDisablingEndsExistingSessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "promoted", ApiRequests.StrongPassword, UserRole.Viewer);
        TestAccounts.Create(factory.Services, "retired", ApiRequests.StrongPassword, UserRole.Viewer);
        using var promoted = await factory.CreateLoggedInClientAsync("promoted", cancellationToken);
        using var retired = await factory.CreateLoggedInClientAsync("retired", cancellationToken);

        TestAccounts.Update(factory.Services, "promoted", user => user.Role = nameof(UserRole.Configurator));
        TestAccounts.Update(factory.Services, "retired", user => user.IsEnabled = false);

        await AssertSessionStatusAsync(promoted, HttpStatusCode.Unauthorized, cancellationToken);
        await AssertSessionStatusAsync(retired, HttpStatusCode.Unauthorized, cancellationToken);
        using var again = await factory.CreateLoggedInClientAsync("promoted", cancellationToken);
        await AssertSessionStatusAsync(again, HttpStatusCode.OK, cancellationToken);
    }

    [Fact]
    public async Task IncrementingTheSessionGenerationEndsEverySession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var admin = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", cancellationToken);

        var generation = factory.Services.GetRequiredService<SessionSecurityState>().IncrementGeneration();

        Assert.Equal(2, generation);
        await AssertSessionStatusAsync(admin, HttpStatusCode.Unauthorized, cancellationToken);
        await AssertSessionStatusAsync(viewer, HttpStatusCode.Unauthorized, cancellationToken);
        using var fresh = await factory.CreateLoggedInClientAsync("admin", cancellationToken);
        await AssertSessionStatusAsync(fresh, HttpStatusCode.OK, cancellationToken);
    }

    [Fact]
    public async Task HttpAndHttpsSessionsCannotBeInterchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var httpCookie = await LoginCookieAsync(factory, Uri.UriSchemeHttp, cancellationToken);
        var httpsCookie = await LoginCookieAsync(factory, Uri.UriSchemeHttps, cancellationToken);

        Assert.StartsWith($"{SessionAuthentication.HttpCookieName}=", httpCookie.Header, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", httpCookie.Header, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith($"{SessionAuthentication.HttpsCookieName}=", httpsCookie.Header, StringComparison.Ordinal);
        Assert.Contains("secure", httpsCookie.Header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", httpsCookie.Header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", httpsCookie.Header, StringComparison.OrdinalIgnoreCase);

        // 各自协议下有效；换到另一种协议（即使改成该协议的 Cookie 名称）都被拒绝。
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(factory, Uri.UriSchemeHttp, SessionAuthentication.HttpCookieName, httpCookie.Value, cancellationToken));
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(factory, Uri.UriSchemeHttps, SessionAuthentication.HttpsCookieName, httpsCookie.Value, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(factory, Uri.UriSchemeHttps, SessionAuthentication.HttpsCookieName, httpCookie.Value, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(factory, Uri.UriSchemeHttp, SessionAuthentication.HttpCookieName, httpsCookie.Value, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(factory, Uri.UriSchemeHttps, SessionAuthentication.HttpCookieName, httpsCookie.Value, cancellationToken));
    }

    private static async Task AssertSessionStatusAsync(HttpClient client, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), cancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<(string Header, string Value)> LoginCookieAsync(TenonEdgeApplicationFactory factory, string scheme, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"{scheme}://localhost"), HandleCookies = false });
        using var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var header = Assert.Single(login.Headers.GetValues(HeaderNames.SetCookie));
        var value = header[(header.IndexOf('=', StringComparison.Ordinal) + 1)..header.IndexOf(';', StringComparison.Ordinal)];
        return (header, value);
    }

    private static async Task<HttpStatusCode> SessionStatusAsync(
        TenonEdgeApplicationFactory factory,
        string scheme,
        string cookieName,
        string cookieValue,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"{scheme}://localhost"), HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRequests.Session, UriKind.Relative));
        request.Headers.Add(HeaderNames.Cookie, $"{cookieName}={cookieValue}");
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }
}
