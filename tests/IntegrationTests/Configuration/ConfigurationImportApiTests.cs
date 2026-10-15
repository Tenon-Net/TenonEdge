using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Configuration;

public sealed class ConfigurationImportApiTests
{
    private const string Endpoint = "/api/edge-admin/v1/configuration/import";
    private const string Empty = """{"formatVersion":1,"connections":[],"devices":[],"inputs":[],"routes":[]}""";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ConfigDatabase Database(IServiceProvider services) => services.GetRequiredService<ConfigDatabase>();
    private static string Version(IServiceProvider services) => Database(services).InTransaction(ConfigurationDraft.ReadVersion);
    private static async Task<HttpResponseMessage> Upload(HttpClient client, string json = Empty, string version = "0", string mode = "merge")
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(Endpoint + "/prepare?expectedVersion=" + Uri.EscapeDataString(version) + "&mode=" + mode, UriKind.Relative), body, Token);
    }
    private static async Task<Guid> Prepare(HttpClient client, string json = Empty, string version = "0", string mode = "merge")
    {
        using var response = await Upload(client, json, version, mode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
    }
    private static Task<HttpResponseMessage> Confirm(HttpClient client, Guid id, bool confirmed = true) =>
        client.PostJsonAsync(Endpoint + "/confirm", new { id, confirmed }, Token);
    private static async Task<HttpClient> Login(TenonEdgeApplicationFactory host, UserRole role = UserRole.Configurator)
    {
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, role);
        return await host.CreateLoggedInClientAsync("operator", Token);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("viewer")]
    [InlineData("csrf")]
    public async Task AllActionsEnforceAuthorizationAndWritesEnforceCsrf(string scenario)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = scenario == "anonymous" ? host.CreateClient() : await Login(host, scenario == "viewer" ? UserRole.Viewer : UserRole.Configurator);
        if (scenario == "csrf")
        { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
        var expected = scenario == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        using var upload = await Upload(client);
        using var confirm = await Confirm(client, Guid.NewGuid());
        using var cancel = await client.DeleteAsync(new Uri(Endpoint + "/" + Guid.NewGuid(), UriKind.Relative), Token);
        using var current = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(expected, upload.StatusCode);
        Assert.Equal(expected, confirm.StatusCode);
        Assert.Equal(expected, cancel.StatusCode);
        Assert.Equal(scenario == "csrf" ? HttpStatusCode.OK : expected, current.StatusCode);
        Assert.Equal("0", Version(host.Services));
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("replace")]
    public async Task ConfirmWritesDraftOnceAndAuditAtomicallyWithoutChangingRuntimeOrSecrets(string mode)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host, UserRole.Administrator);
        var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "receiver", "Receiver", ConnectionKind.Http, Http: new("https://example.com/")), "fixture", null);
        var secret = host.Services.GetRequiredService<HttpCredentialStore>().Write(connection.Connection.Id,
            new("https://example.com/", HttpCredentialKind.Bearer, Secret: "fixture-not-real", ExpectedDraftVersion: connection.Version), "fixture", null, true);
        const string file = """{"formatVersion":1,"connections":[],"devices":[{"deviceKey":"meter","name":"Meter","kind":"simulator","enabled":true,"addressNotation":"offset","group":null,"connectionKey":null,"unitId":null}],"inputs":[{"deviceKey":"meter","inputKey":"values","name":"Values","kind":"simulator","enabled":true,"modbus":null,"mqtt":null,"http":null,"simulator":{"sampleJson":"{\"value\":9223372036854775807}"}}],"routes":[]}""";
        var id = await Prepare(client, file, connection.Version, mode);
        Assert.Equal(connection.Version, Version(host.Services));
        var review = (await client.GetJsonAsync(Endpoint, Token)).GetProperty("review");
        Assert.Equal(mode, review.GetProperty("mode").GetString());
        Assert.Equal(mode == "replace" ? 3 : 2, review.GetProperty("changes").GetArrayLength());
        using (var unconfirmed = await Confirm(client, id, false))
        { Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode); }
        using var response = await Confirm(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var version = (await response.ReadJsonAsync(Token)).GetProperty("version").GetString();
        Assert.NotEqual(connection.Version, version);
        Assert.Equal(version, Version(host.Services));
        Assert.Equal(mode == "replace" ? 0 : 1, Database(host.Services).Client.Queryable<ConnectionDraftRecord>().Count());
        Assert.Contains("9223372036854775807", Database(host.Services).Client.Queryable<TenonEdge.Host.Features.Inputs.InputDraftRecord>().Single().Content, StringComparison.Ordinal);
        Assert.Equal(secret, host.Services.GetRequiredService<HttpCredentialStore>().Read(connection.Connection.Id));
        var audit = Assert.Single(TestAccounts.Audit(host.Services, "configuration.imported"));
        Assert.DoesNotContain("fixture-not-real", audit.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("9223372036854775807", audit.Detail!, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await client.GetJsonAsync(Endpoint, Token)).GetProperty("review").ValueKind);
        using var replay = await Confirm(client, id);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Single(TestAccounts.Audit(host.Services, "configuration.imported"));
        var state = Database(host.Services).Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PendingRevision);
    }

    [Fact]
    public async Task CandidateIsOwnedBySessionNotAccountAndReplacementInvalidatesOldReview()
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var owner = await Login(host);
        using var other = await host.CreateLoggedInClientAsync("operator", Token);
        var id = await Prepare(owner);
        Assert.Equal(JsonValueKind.Null, (await other.GetJsonAsync(Endpoint, Token)).GetProperty("review").ValueKind);
        using (var blocked = await Upload(other))
        { Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); }
        using (var confirm = await Confirm(other, id))
        { Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode); }
        using (var cancel = await other.DeleteAsync(new Uri(Endpoint + "/" + id, UriKind.Relative), Token))
        { Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode); }
        using (var invalid = await Upload(owner, "{"))
        { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        using (var old = await Confirm(owner, id))
        { Assert.Equal(HttpStatusCode.NotFound, old.StatusCode); }
        id = await Prepare(other);
        using (var cancelled = await other.DeleteAsync(new Uri(Endpoint + "/" + id, UriKind.Relative), Token))
        { Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode); }
        using (var replay = await Confirm(other, id))
        { Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode); }
        Assert.Equal("0", Version(host.Services));
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("replace")]
    public async Task ConnectionKindConflictReportsExactPathAndDoesNotReplaceDraft(string mode)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host);
        var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "receiver", "Receiver", ConnectionKind.Http, Http: new("https://example.com/")), "fixture", null);
        const string file = """{"formatVersion":1,"connections":[{"connectionKey":"receiver","name":"Receiver","kind":"mqtt","enabled":true,"modbus":null,"http":null,"credential":null,"mqtt":{"host":"broker.example.com"}}],"devices":[],"inputs":[],"routes":[]}""";
        using var result = await Upload(client, file, connection.Version, mode);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        var errors = (await result.ReadJsonAsync(Token)).GetProperty("errors");
        Assert.Equal("import.connectionKindImmutable", errors.GetProperty("$.connections[0].kind")[0].GetString());
        Assert.Equal(connection.Version, Version(host.Services));
        Assert.Equal(JsonSerializer.Serialize(connection.Connection), JsonSerializer.Serialize(Assert.Single(host.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections)));
        Assert.Equal(JsonValueKind.Null, (await client.GetJsonAsync(Endpoint, Token)).GetProperty("review").ValueKind);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("credential")]
    [InlineData("audit")]
    public async Task ConflictOrAuditFailureRollsBackAndConsumesConfirmation(string change)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host);
        var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "receiver", "Receiver", ConnectionKind.Http, Http: new("https://example.com/")), "fixture", null);
        var id = await Prepare(client, Empty, connection.Version, "replace");
        if (change == "draft")
        { host.Services.GetRequiredService<ConnectionDraftStore>().Write(null, new(connection.Version, "other", "Other", ConnectionKind.Http, Http: new("https://example.org/")), "fixture", null); }
        if (change == "credential")
        { host.Services.GetRequiredService<HttpCredentialStore>().Write(connection.Connection.Id, new("https://example.com/", HttpCredentialKind.Bearer, Secret: "fixture", ExpectedDraftVersion: connection.Version), "fixture", null, true); }
        if (change == "audit")
        { Database(host.Services).Client.Ado.ExecuteCommand("CREATE TRIGGER reject_import BEFORE INSERT ON audit_log WHEN NEW.action='configuration.imported' BEGIN SELECT RAISE(ABORT, 'fixture'); END;"); }
        var before = JsonSerializer.Serialize(Database(host.Services).InTransaction(sql => ConfigurationCheckpoint.Read(sql, ConfigurationDraft.ReadVersion(sql))));
        using var result = await Confirm(client, id);
        Assert.Equal(change == "audit" ? HttpStatusCode.InternalServerError : HttpStatusCode.Conflict, result.StatusCode);
        using var replay = await Confirm(client, id);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(Database(host.Services).InTransaction(sql => ConfigurationCheckpoint.Read(sql, ConfigurationDraft.ReadVersion(sql)))));
        Assert.Empty(TestAccounts.Audit(host.Services, "configuration.imported"));
    }

    [Fact]
    public async Task UploadChecksContentTypeModeVersionSizeAndStrictConfirmationSchema()
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host);
        using var wrongBody = new StringContent(Empty, Encoding.UTF8, "text/plain");
        using var wrong = await client.PostAsync(new Uri(Endpoint + "/prepare?expectedVersion=0", UriKind.Relative), wrongBody, Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrong.StatusCode);
        using (var mode = await Upload(client, mode: "0"))
        { Assert.Equal(HttpStatusCode.BadRequest, mode.StatusCode); }
        using (var version = await Upload(client, version: "old"))
        { Assert.Equal(HttpStatusCode.Conflict, version.StatusCode); }
        using (var large = await Upload(client, Empty.PadRight(ConfigurationExport.MaximumBytes + 1)))
        { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode); }
        var id = await Prepare(client, Empty.PadRight(ConfigurationExport.MaximumBytes));
        using var extra = await client.PostJsonAsync(Endpoint + "/confirm", new { id, confirmed = true, mode = "replace" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(id, (await client.GetJsonAsync(Endpoint, Token)).GetProperty("review").GetProperty("id").GetGuid());
        Assert.Equal("0", Version(host.Services));
    }

    [Theory]
    [InlineData("budget", 429)]
    [InlineData("busy", 409)]
    public async Task RejectedUploadDoesNotReadRequestBody(string kind, int status)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host);
        using var login = await client.LoginAsync("operator", ApiRequests.StrongPassword, Token);
        var session = await client.UseSessionAsync(login, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        if (kind == "busy")
        {
            using var second = await host.CreateLoggedInClientAsync("operator", Token);
            await Prepare(second);
        }
        var budget = host.Services.GetRequiredService<PreviewOperationBudget>();
        var leases = kind == "budget" ? Enumerable.Range(0, 4).Select(index => budget.TryAcquire("fixture-" + index)!).ToArray() : [];
        using var body = new ProbeStream();
        try
        {
            var response = await host.Server.SendAsync(context =>
            {
                context.Request.Method = "POST";
                context.Request.Path = Endpoint + "/prepare";
                context.Request.QueryString = new("?expectedVersion=0");
                context.Request.ContentType = "application/json";
                context.Request.Headers.Cookie = cookie;
                context.Request.Headers[ApiRequests.CsrfHeader] = session.GetProperty("csrfToken").GetString();
                context.Request.Body = body;
            }, Token);
            Assert.Equal(status, response.Response.StatusCode);
            Assert.False(body.ReadAttempted);
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
    }

    [Fact]
    public async Task ApiTokenOwnsItsCandidateSeparatelyAndAuditNamesTheToken()
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var cookie = await Login(host, UserRole.Administrator);
        var user = TestAccounts.Find(host.Services, "operator")!;
        using var scope = host.Services.CreateScope();
        var created = scope.ServiceProvider.GetRequiredService<ApiTokenStore>().Create(new("import-fixture", UserRole.Configurator, null), new(user.Id, user.SecurityStamp, null));
        using var tokenClient = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Secret);
        var id = await Prepare(tokenClient);
        Assert.Equal(JsonValueKind.Null, (await cookie.GetJsonAsync(Endpoint, Token)).GetProperty("review").ValueKind);
        using (var denied = await Confirm(cookie, id))
        { Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); }
        using var result = await Confirm(tokenClient, id);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var audit = Assert.Single(TestAccounts.Audit(host.Services, "configuration.imported"));
        Assert.Equal("import-fixture", audit.TokenName);
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("disabled")]
    [InlineData("viewer")]
    public async Task CommitRechecksActorInsideTransactionEvenIfRequestWasAlreadyAuthenticated(string change)
    {
        await using var host = new TenonEdgeApplicationFactory();
        using var client = await Login(host);
        var user = TestAccounts.Find(host.Services, "operator")!;
        var actor = new ManagementActor(user.Id, user.SecurityStamp, null);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(Empty));
        var service = host.Services.GetRequiredService<ConfigurationImportPreparation>();
        var review = await service.PrepareAsync(bytes, ConfigurationImportMode.Merge, "0", "captured-owner", Token);
        TestAccounts.Update(host.Services, "operator", row =>
        {
            if (change == "stamp")
            { row.SecurityStamp = SecurityStamps.New(); }
            if (change == "disabled")
            { row.IsEnabled = false; }
            if (change == "viewer")
            { row.Role = nameof(UserRole.Viewer); }
        });
        Assert.Throws<ApiTokenException>(() => service.Confirm(review.Id, true, "captured-owner", actor, Token));
        Assert.Null(service.Current("captured-owner"));
        Assert.Equal("0", Version(host.Services));
        Assert.Empty(TestAccounts.Audit(host.Services, "configuration.imported"));
    }

    private sealed class ProbeStream : MemoryStream
    {
        public bool ReadAttempted { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Rejected upload must not be read.");
        }
    }
}
