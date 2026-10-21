using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class OutputMetadataReadTests
{
    private const long ConnectionId = 9007199254740991;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpIdentityReadsNullableMetadataAndSeesEachCommittedChangeWithoutResolvingSecrets(bool dedicated)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        IHttpCredentialResolver source = factory.Services.GetRequiredService<HttpCredentialStore>();
        using var session = dedicated ? source.OpenSenderReadSession() : null;
        if (dedicated)
        { Assert.NotNull(session); }
        var store = session ?? source;
        Assert.Null(store.DescribeHttp(ConnectionId));
        using var client = database.CreateIsolatedClient();
        var record = new HttpCredentialRecord
        {
            ConnectionId = ConnectionId,
            Recipient = "https://example.invalid/",
            Kind = (int)HttpCredentialKind.Basic,
            Username = "fixture-user",
            AccountMarker = "first-account",
            Version = "first-version",
            // 无效保护串：Describe 只读身份，不应解析、返回或依赖秘密。
            ProtectedSecret = "invalid-fixture-ciphertext",
        };
        client.Insertable(record).ExecuteCommand();
        Assert.Equal(new(HttpCredentialKind.Basic, "fixture-user", null, record.AccountMarker, record.Version), store.DescribeHttp(ConnectionId));
        Assert.Null(store.DescribeHttp(1));

        record.Kind = (int)HttpCredentialKind.ClientCertificate;
        record.Username = null;
        record.CertificateSubject = "CN=fixture-client";
        record.AccountMarker = "second-account";
        record.Version = "second-version";
        client.Updateable(record).ExecuteCommand();
        Assert.Equal(new(HttpCredentialKind.ClientCertificate, null, null, record.AccountMarker, record.Version, record.CertificateSubject), store.DescribeHttp(ConnectionId));

        record.Kind = (int)HttpCredentialKind.ApiKeyHeader;
        record.Name = "X-Fixture-Key";
        record.CertificateSubject = null;
        record.Version = "third-version";
        client.Updateable(record).ExecuteCommand();
        Assert.Equal(new(HttpCredentialKind.ApiKeyHeader, null, record.Name, record.AccountMarker, record.Version), store.DescribeHttp(ConnectionId));
        client.Deleteable(record).ExecuteCommand();
        Assert.Null(store.DescribeHttp(ConnectionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputControlReadsOnlySelectedConnectionAndSeesCommittedPauseResumeAndDeletion(bool dedicated)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        IOutputControl source = factory.Services.GetRequiredService<OutputControlStore>();
        using var session = dedicated ? source.OpenSenderReadSession() : null;
        if (dedicated)
        { Assert.NotNull(session); }
        var store = session ?? source;
        Assert.Equal(new(false, null), store.Read(ConnectionId));
        using var client = database.CreateIsolatedClient();
        var record = new OutputControlRecord { ConnectionId = ConnectionId, ManuallyPaused = true, Version = "paused" };
        client.Insertable(record).ExecuteCommand();
        Assert.Equal(new(true, "paused"), store.Read(ConnectionId));
        Assert.Equal(new(false, null), store.Read(1));
        record.ManuallyPaused = false;
        record.Version = "resumed";
        client.Updateable(record).ExecuteCommand();
        Assert.Equal(new(false, "resumed"), store.Read(ConnectionId));
        client.Deleteable(record).ExecuteCommand();
        Assert.Equal(new(false, null), store.Read(ConnectionId));
    }
}
