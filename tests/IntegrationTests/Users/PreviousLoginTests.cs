using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Users;

public sealed class PreviousLoginTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviousLoginBelongsToTheSessionNotTheLatestDatabaseLoginAndSurvivesPasswordChange()
    {
        var prior = new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);
        var now = prior.AddHours(1);
        var time = new ManualTimeProvider(now);
        await using var parent = new TenonEdgeApplicationFactory();
        await using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer, mustChangePassword: true);
        TestAccounts.Update(factory.Services, "target", user => { user.LastLoginAt = prior.ToUnixTimeMilliseconds(); user.LastLoginIp = "192.0.2.18"; });
        using var first = await factory.CreateLoggedInClientAsync("target", Token);
        var original = await first.GetJsonAsync(ApiRequests.Session, Token);
        Assert.Equal(prior, original.GetProperty("previousLoginAt").GetDateTimeOffset());
        Assert.Equal("192.0.2.18", original.GetProperty("previousLoginIp").GetString());
        time.Advance(TimeSpan.FromMinutes(5));
        using var second = await factory.CreateLoggedInClientAsync("target", Token);
        var recent = await second.GetJsonAsync(ApiRequests.Session, Token);
        Assert.Equal(now, recent.GetProperty("previousLoginAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, recent.GetProperty("previousLoginIp").ValueKind); // TestServer未配置来源地址，不能伪造。
        Assert.Equal(prior, (await first.GetJsonAsync(ApiRequests.Session, Token)).GetProperty("previousLoginAt").GetDateTimeOffset());
        using var changed = await first.PostJsonAsync(ApiRequests.ChangePassword, new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var reissued = await first.UseSessionAsync(changed, Token);
        Assert.False(reissued.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(prior, reissued.GetProperty("previousLoginAt").GetDateTimeOffset());
        Assert.Equal("192.0.2.18", reissued.GetProperty("previousLoginIp").GetString());
        var refreshed = await first.GetJsonAsync(ApiRequests.Session, Token);
        Assert.Equal(prior, refreshed.GetProperty("previousLoginAt").GetDateTimeOffset());
        using var expired = await second.GetAsync(new Uri(ApiRequests.Session, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task FirstSetupAndFirstLoginHaveNoInventedPreviousLogin()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        using var setup = await client.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var session = await client.UseSessionAsync(setup, Token);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("previousLoginAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("previousLoginIp").ValueKind);
        TestAccounts.Create(factory.Services, "target", ApiRequests.StrongPassword, UserRole.Viewer);
        using var login = await client.LoginAsync("target", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var first = await login.ReadJsonAsync(Token);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previousLoginAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previousLoginIp").ValueKind);
    }
}
