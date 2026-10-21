using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpOutputSendIdentityTests
{
    private static readonly string[] Inputs = ["device/input"];
    [Fact]
    public async Task SenderScopeUsesItsOwnMetadataInBothIdentityChecksAndTheAdapter()
    {
        using var reader = new Reader();
        var source = new Credentials
        {
            OnDescribe = _ => throw new InvalidOperationException("Must use the sender metadata reader"),
            OnOpen = () => reader,
        };
        var settings = Settings();
        IOutputTarget original = Target(settings, source);
        using var scope = original.OpenSenderReadSession();
        Assert.NotNull(scope);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(original.Id, scope.Target.Id);
        Assert.Equal(original.ConnectionId, scope.Target.ConnectionId);
        Assert.Equal(settings.Fingerprint(null), scope.Target.CurrentFingerprint);
        Assert.Equal(new(settings.Fingerprint(null), "anonymous"), scope.Target.ReadSendIdentity());
        await using var adapter = scope.Target.CreateAdapter();
        // 适配器必须在网络之前独立读取当前身份；故意不符的指纹不能发出。
        var result = await adapter.SendAsync(new(new byte[] { 1 }, "capture", null, "wrong-fingerprint", "unused"),
            TestContext.Current.CancellationToken);
        Assert.True(result.FingerprintMismatch);
        Assert.Equal(3, reader.Reads);
        Assert.Equal(0, source.Reads);
        reader.Identity = new(HttpCredentialKind.Basic, "new-account", null, "account", "rotated");
        Assert.Equal(new(settings.Fingerprint(reader.Identity), "rotated"), scope.Target.ReadSendIdentity());
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scope.Target.ReadSendIdentity());
    }

    [Fact]
    public void ResolverWithoutDedicatedReaderKeepsTheExistingTargetPath()
    {
        IOutputTarget target = Target(Settings(), new Credentials { OnDescribe = _ => null });
        Assert.Null(target.OpenSenderReadSession());
    }

    private sealed class Reader : IHttpCredentialReadSession
    {
        public int Reads { get; private set; }
        public HttpCredentialIdentity? Identity { get; set; }
        private bool _disposed;
        public HttpCredentialIdentity? DescribeHttp(long connectionId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Reads++;
            return Identity;
        }
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => throw new InvalidOperationException("Anonymous adapter must not resolve secrets");
        public void Dispose() => _disposed = true;
    }
    [Fact]
    public void FingerprintAndVersionComeFromOneCurrentMetadataReadAndNextCallReadsAgain()
    {
        var first = new HttpCredentialIdentity(HttpCredentialKind.Basic, "first", null, "account-1", "v1");
        var second = first with { Username = "second", AccountMarker = "account-2", Version = "v2" };
        var credentials = new Credentials { OnDescribe = n => n == 1 ? first : second };
        var settings = Settings();
        IOutputTarget target = Target(settings, credentials);
        Assert.Equal(new(settings.Fingerprint(first), "v1"), target.ReadSendIdentity());
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(new(settings.Fingerprint(second), "v2"), target.ReadSendIdentity());
        Assert.Equal(2, credentials.Reads);
    }

    [Fact]
    public void AnonymousCreationAndDeletionAreNotCachedAndNoSecretIsResolved()
    {
        var identity = new HttpCredentialIdentity(HttpCredentialKind.Bearer, null, null, "account", "v1");
        var credentials = new Credentials { OnDescribe = n => n == 2 ? identity : null };
        var settings = Settings();
        IOutputTarget target = Target(settings, credentials);
        Assert.Equal(new(settings.Fingerprint(null), "anonymous"), target.ReadSendIdentity());
        Assert.Equal(new(settings.Fingerprint(identity), "v1"), target.ReadSendIdentity());
        Assert.Equal(new(settings.Fingerprint(null), "anonymous"), target.ReadSendIdentity());
        Assert.Equal(3, credentials.Reads);
    }

    [Fact]
    public void HttpsStillReadsCurrentTrustVersionAndPropagatesUnavailableTrust()
    {
        var credentials = new Credentials { OnDescribe = _ => null };
        var settings = Settings("https");
        var trust = new Trust();
        IOutputTarget target = Target(settings, credentials, trust);
        Assert.Equal(new(settings.Fingerprint(null), "anonymous:tls:t1"), target.ReadSendIdentity());
        trust.Version = "t2";
        Assert.Equal(new(settings.Fingerprint(null), "anonymous:tls:t2"), target.ReadSendIdentity());
        trust.Fail = true;
        Assert.Throws<InvalidOperationException>(() => target.ReadSendIdentity());
        Assert.Equal(3, credentials.Reads);
        Assert.Equal(3, trust.Reads);
    }

    private static HttpOutputSettings Settings(string scheme = "http") => HttpOutputSettings.Parse(JsonSerializer.SerializeToElement(new
    { connectionId = 1, baseAddress = $"{scheme}://example.invalid/", path = "capture", inputs = Inputs }));

    private static HttpOutputTarget Target(HttpOutputSettings settings, Credentials credentials, Trust? trust = null) =>
        new HttpOutputTarget(RuntimeUnitId.Create("output.http", "test"), settings, credentials, TimeProvider.System,
            new NoNetwork(), new SharedConnections(), trust);

    private sealed class Credentials : IHttpCredentialResolver
    {
        internal Func<IHttpCredentialReadSession?>? OnOpen { get; init; }
        public IHttpCredentialReadSession? OpenSenderReadSession() => OnOpen?.Invoke();
        internal required Func<int, HttpCredentialIdentity?> OnDescribe { get; init; }
        internal int Reads { get; private set; }
        public HttpCredentialIdentity? DescribeHttp(long connectionId)
        {
            Assert.Equal(1, connectionId);
            return OnDescribe(++Reads);
        }
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => throw new InvalidOperationException("Must not resolve secrets");
    }

    private sealed class Trust : ITlsTrustResolver
    {
        internal string Version { get; set; } = "t1";
        internal bool Fail { get; set; }
        internal int Reads { get; private set; }
        public TlsTrustSnapshot? ResolveTlsTrust(long connectionId, string recipient)
        {
            Assert.Equal(1, connectionId);
            Assert.Equal("https://example.invalid/", recipient);
            Reads++;
            if (Fail)
            { throw new InvalidOperationException("Trust unavailable"); }
            return new(TlsTrustMode.PinnedCertificate, "unused-public-fixture", Version);
        }
    }

    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must not connect");
    }
}
