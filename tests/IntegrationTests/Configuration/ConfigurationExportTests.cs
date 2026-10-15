using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Configuration;

public sealed class ConfigurationExportTests
{
    private const string Endpoint = "/api/edge-admin/v1/configuration/export";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task ExportIsProtectedByBackendRoleAndNeverIncludesHostState(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
        { return; }
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("tenonedge-configuration.json", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var value = await response.ReadJsonAsync(Token);
        Assert.Equal(["formatVersion", "connections", "devices", "inputs", "routes"], value.EnumerateObject().Select(property => property.Name));
        Assert.Empty(value.GetProperty("connections").EnumerateArray());
        var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PendingRevision);
    }

    [Fact]
    public async Task RealCredentialRowsProduceOnlyRefillHintsAndAccountDigestsAndDraftIsUnchanged()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var http = connections.Write(null, new("0", "http", "HTTP", ConnectionKind.Http, Http: new("https://example.com/base/")), "fixture", null);
        var mqtt = connections.Write(null, new(http.Version, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("broker.example.com")), "fixture", null);
        await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(http.Connection.Id,
            new("https://example.com/base/", HttpCredentialKind.Bearer, Secret: "not-a-real-http-secret", ExpectedDraftVersion: mqtt.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        await factory.Services.GetRequiredService<MqttCredentialStore>().WriteAsync(mqtt.Connection.Id,
            new("mqtt://broker.example.com:1883", Username: "private-fixture-username", Password: "not-a-real-mqtt-secret", ExpectedDraftVersion: mqtt.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(mqtt.Version, "meter", "Meter", DeviceKind.Mqtt, ConnectionId: mqtt.Connection.Id), "fixture", null);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        var before = db.InTransaction(sql => ConfigurationCheckpoint.Read(sql, device.Version));
        var record = db.Client.Queryable<MqttCredentialRecord>().InSingle(mqtt.Connection.Id);
        using var response = await client.GetAsync(new Uri(Endpoint + "?expectedVersion=" + device.Version, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        using var parsed = JsonDocument.Parse(text);
        Assert.Equal("mqtt", parsed.RootElement.GetProperty("devices")[0].GetProperty("connectionKey").GetString());
        foreach (var connection in parsed.RootElement.GetProperty("connections").EnumerateArray())
        {
            Assert.Equal("requiresInput", connection.GetProperty("credential").GetProperty("status").GetString());
            Assert.Matches("^sha256:[a-f0-9]{64}$", connection.GetProperty("credential").GetProperty("accountIdentity").GetString()!);
            Assert.False(connection.TryGetProperty("id", out _));
        }
        foreach (var secret in new[] { "not-a-real-http-secret", "not-a-real-mqtt-secret", record.ProtectedPassword!, record.AccountMarker,
            record.Version, "private-fixture-username", device.Version })
        { Assert.DoesNotContain(secret, text, StringComparison.Ordinal); }
        var after = db.InTransaction(sql => ConfigurationCheckpoint.Read(sql, device.Version));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal("not-a-real-mqtt-secret", factory.Services.GetRequiredService<MqttCredentialStore>().ResolveMqtt(mqtt.Connection.Id, record.Recipient)!.Password);
        using var stale = await client.GetAsync(new Uri(Endpoint + "?expectedVersion=0", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null(stale.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task AccountComparisonDigestIgnoresApiKeyParameterNameAndSecretRotationButNotAccountChanges()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var saved = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "HTTP", ConnectionKind.Http, Http: new("https://example.com/base/")), "fixture", null);
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        var first = await credentials.WriteAsync(saved.Connection.Id, new("https://example.com/base/", HttpCredentialKind.ApiKeyHeader,
            Name: "X-First-Key", Secret: "fixture-first-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        async Task<string> Digest()
        {
            using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var value = await response.ReadJsonAsync(Token);
            return value.GetProperty("connections")[0].GetProperty("credential").GetProperty("accountIdentity").GetString()!;
        }
        var original = await Digest();
        var rotated = await credentials.WriteAsync(saved.Connection.Id, new("https://example.com/base/", HttpCredentialKind.ApiKeyHeader,
            Name: "X-Second-Key", Secret: "fixture-rotated-secret", ExpectedVersion: first.Version,
            ExpectedDraftVersion: saved.Version, AccountChanged: false), "fixture", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Version, rotated.Version);
        Assert.Equal(original, await Digest());
        await credentials.WriteAsync(saved.Connection.Id, new("https://example.com/base/", HttpCredentialKind.ApiKeyHeader,
            Name: "X-Second-Key", Secret: "fixture-other-account", ExpectedVersion: rotated.Version,
            ExpectedDraftVersion: saved.Version, AccountChanged: true), "fixture", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(original, await Digest());
    }

    [Fact]
    public async Task MetadataProjectionReportsActualRecipientAndSecretPresenceWithoutRebindingToEditedDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var saved = store.Write(null, new("0", "http", "HTTP", ConnectionKind.Http, Http: new("https://example.com/base/")), "fixture", null);
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        var first = await credentials.WriteAsync(saved.Connection.Id, new("https://example.com/base/", HttpCredentialKind.Bearer,
            Secret: "fixture-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var changed = store.Write(saved.Connection.Id, new(saved.Version, "http", "HTTP", ConnectionKind.Http,
            Http: new("https://new.example.com/")), "fixture", null);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var actual = Assert.Single(database.InTransaction(ConfigurationCredentialMetadata.Read));
        Assert.True(actual.HasSecret);
        Assert.Equal("https://example.com/base/", actual.Recipient);
        Assert.Equal(first.Version, actual.Version);
        await credentials.WriteAsync(saved.Connection.Id, new("https://new.example.com/", ExpectedVersion: first.Version,
            ExpectedDraftVersion: changed.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var invalidated = Assert.Single(database.InTransaction(ConfigurationCredentialMetadata.Read));
        Assert.False(invalidated.HasSecret);
        Assert.Equal("https://new.example.com/", invalidated.Recipient);
        Assert.NotEqual(first.Version, invalidated.Version);
    }

    [Fact]
    public async Task SharedPreviewBudgetRejectsExcessExportAndUnresolvedGraphNeverReturnsAPartialFile()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using (var one = budget.TryAcquire("fixture-1"))
        using (var two = budget.TryAcquire("fixture-2"))
        using (var three = budget.TryAcquire("fixture-3"))
        using (var four = budget.TryAcquire("fixture-4"))
        {
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.NotNull(three);
            Assert.NotNull(four);
            using var busy = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        }
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var route = routes.Write(null, new("0", "broken", "Broken reference", false,
            new(1, [new("source", "input.simulator", 1, "source", JsonSerializer.Deserialize<JsonElement>("{\"sourceIds\":[999]}"))], [], new([]))), "fixture", null);
        using var response = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("configuration.exportReferenceMissing", await response.ReadProblemCodeAsync(Token));
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal(route.Version, routes.Read().Version);
        // The budget lease is released even when validation fails.
        using var released = budget.TryAcquire("after-failure");
        Assert.NotNull(released);
    }
}
