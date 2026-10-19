using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed partial class ModuleCredentialTests
{
    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task ModuleReviewRequiresWriteRoleAndCsrfAndRejectsSecretFields(string role)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var saved = Create(host.Services);
        var path = Endpoint(saved.Connection.Id) + "/review";
        var request = new ModuleCredentialReviewRequest(saved.Version, Account: "wire-user");
        using var anonymous = host.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync(path, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await client.PostAsJsonAsync(path, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        using var response = await client.PostAsJsonAsync(path, request, Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        if (role != "Viewer")
        {
            Assert.True(response.Headers.CacheControl!.NoStore);
            var review = (await response.Content.ReadFromJsonAsync<ModuleCredentialReview>(Token))!;
            Assert.Null(review.Current);
            Assert.False(review.AccountDeclarationRequired);
            Assert.Equal(123, review.RecipientParameters.GetProperty("port").GetInt32());
            Assert.False(review.RecipientParameters.TryGetProperty("timeout", out _));
            using var secret = await client.PostAsJsonAsync(path, new { expectedDraftVersion = saved.Version, account = "wire-user", secrets = new { auth = Secret } }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
            Assert.DoesNotContain(Secret, await secret.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }
        Assert.Null(host.Services.GetRequiredService<ModuleCredentialStore>().Read(saved.Connection.Id));
        Assert.Empty(TestAccounts.Audit(host.Services, "credential.module.write"));
    }

    [Fact]
    public async Task ModuleReviewIsDynamicAndDoesNotAuthorizeAnOutdatedWriteOrRetain()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        var saved = Create(host.Services);
        var store = host.Services.GetRequiredService<ModuleCredentialStore>();
        var initial = await store.WriteAsync(saved.Connection.Id, Material(saved.Version), "fixture", null, true, Token);
        var request = new ModuleCredentialReviewRequest(saved.Version, initial.Identity.Version, "wire-user");
        Assert.False(store.Review(saved.Connection.Id, request, false).AccountDeclarationRequired);
        var cache = host.Services.GetRequiredService<DeliveryCache>();
        var item = new DeliveryDraft(Guid.NewGuid(), saved.Connection.Id, Guid.NewGuid(), "meter", 1, Encoding.UTF8.GetBytes("{}"), "fixture", "fixture", "fixture", 1);
        Assert.True((await cache.EnqueueAsync([item], Token)).Committed);
        var now = store.Review(saved.Connection.Id, request, false);
        Assert.Equal(1, now.PendingCount);
        Assert.True(now.AccountDeclarationRequired);
        Assert.Equal("credential.accountDeclarationRequired", (await Assert.ThrowsAsync<CredentialWriteException>(() =>
            store.WriteAsync(saved.Connection.Id, Material(saved.Version, initial.Identity.Version, NextSecret), "fixture", null, true, Token))).Code);
        var changed = host.Services.GetRequiredService<ConnectionDraftStore>().Write(saved.Connection.Id, Draft(saved.Version, 124), "fixture", null);
        Assert.Equal("credential.draftConflict", Assert.Throws<CredentialWriteException>(() => store.Review(saved.Connection.Id, request, false)).Code);
        var retain = new ModuleCredentialReviewRequest(changed.Version, initial.Identity.Version, RetainExisting: true);
        Assert.Equal("credential.administratorRequired", Assert.Throws<CredentialWriteException>(() => store.Review(saved.Connection.Id, retain, false)).Code);
        var reviewed = store.Review(saved.Connection.Id, retain, true);
        Assert.True(reviewed.IdentityChanged);
        Assert.True(reviewed.RetainExisting);
        Assert.Equal(124, reviewed.RecipientParameters.GetProperty("port").GetInt32());
        Assert.Equal(initial.Identity, reviewed.Current);
        Assert.Equal(initial.Identity, store.Read(saved.Connection.Id)!.Identity);
        Assert.Equal(item.DeliveryId, Assert.Single(cache.ReadPending(saved.Connection.Id, 0, 1)).DeliveryId);
    }
}
