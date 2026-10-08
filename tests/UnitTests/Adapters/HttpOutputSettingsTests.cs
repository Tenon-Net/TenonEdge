using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpOutputSettingsTests
{
    private static readonly string[] Inputs = ["device/input"];
    [Theory]
    [InlineData("https://example.com/api", "events", "https://example.com/api/events")]
    [InlineData("https://example.com/api/", "/events?fixed=1", "https://example.com/api/events?fixed=1")]
    [InlineData("http://[::1]:8081/", "", "http://[::1]:8081/")]
    public void BasePathIsPreservedAndLeadingSlashDoesNotEscapeIt(string basis, string path, string expected) =>
        Assert.Equal(expected, Parse(basis, path).Endpoint.AbsoluteUri);

    [Theory]
    [InlineData("http://user:secret@example.com/", "events")]
    [InlineData("file:///tmp/", "events")]
    [InlineData("http://example.com:0/", "events")]
    [InlineData("https://example.com/?secret=1", "events")]
    [InlineData("https://example.com/#fragment", "events")]
    [InlineData("https://example.com/api/", "../outside")]
    [InlineData("https://example.com/api/", "https://other.example/")]
    [InlineData("https://example.com/api/", "//other.example/")]
    [InlineData("https://example.com/api/", "%2e%2e/outside")]
    [InlineData("https://example.com/api/", "%252e%252e/outside")]
    [InlineData("https://example.com/api/", "%2f../outside")]
    [InlineData("https://example.com/api/", "..\\outside")]
    [InlineData("https://example.com/api/", "events#fragment")]
    [InlineData("https://example.com/api/", "events/{deviceKey}")]
    public void InvalidRecipientOrEscapingPathIsRejected(string basis, string path) => Assert.ThrowsAny<ArgumentException>(() => Parse(basis, path));

    [Theory]
    [InlineData("Authorization", "Bearer not-allowed")]
    [InlineData("Cookie", "session=secret")]
    [InlineData("Host", "other.example")]
    [InlineData("Content-Length", "1")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Idempotency-Key", "override")]
    [InlineData("X-Api-Key", "secret")]
    [InlineData("X-Test", "bad\r\nInjected: 1")]
    [InlineData("X-Test", "bad\u0001value")]
    public void FrozenHeadersCannotCarrySecretsOrOverrideRequestFraming(string name, string value)
    {
        var json = JsonSerializer.SerializeToElement(new { connectionId = 1, baseAddress = "http://example.com/", inputs = Inputs, headers = new Dictionary<string, string> { [name] = value } });
        Assert.ThrowsAny<ArgumentException>(() => HttpOutputSettings.Parse(json));
    }

    [Fact]
    public void CaseInsensitiveDuplicateHeadersAndUnknownSecretFieldsAreRejected()
    {
        using var duplicate = JsonDocument.Parse("""{"connectionId":1,"baseAddress":"http://example.com/","inputs":["device/input"],"headers":{"X-Test":"a","x-test":"b"}}""");
        Assert.Throws<ArgumentException>(() => HttpOutputSettings.Parse(duplicate.RootElement));
        using var secret = JsonDocument.Parse("""{"connectionId":1,"baseAddress":"http://example.com/","inputs":["device/input"],"password":"secret"}""");
        Assert.Throws<JsonException>(() => HttpOutputSettings.Parse(secret.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TotalDeadlineCoversQueueWaitAndCallerCancellationIsNotConvertedToRetry(bool callerCancels)
    {
        var settings = Parse("http://never.invalid/", "events") with { TimeoutMs = 1000 };
        var time = new FakeTimeProvider();
        var shared = new SharedConnections();
        await using var owner = shared.Acquire(1, "http", "test", () => new HttpConnectionSession(new NoNetwork(), 1000));
        var borrower = shared.Acquire<HttpConnectionSession>(1, "http", "test", () => throw new InvalidOperationException("must share"));
        await using var adapter = new HttpOutputAdapter(borrower, settings, new NoCredentials(), time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = owner.ExecuteAsync(async (_, _) => { await release.Task; return true; }, TestContext.Current.CancellationToken);
        try
        {
            using var caller = new CancellationTokenSource();
            var waiting = adapter.SendAsync(new OutputMessage(ReadOnlyMemory<byte>.Empty, "events", null, "unused", "unused"), caller.Token);
            Assert.False(waiting.IsCompleted);
            if (callerCancels)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            else
            {
                time.Advance(TimeSpan.FromMilliseconds(999));
                Assert.False(waiting.IsCompleted);
                time.Advance(TimeSpan.FromMilliseconds(1));
                var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(OutputSendStatus.Retry, result.Status);
                Assert.False(result.CountsTowardPoison);
            }
            Assert.False(holding.IsCompleted);
        }
        finally { release.TrySetResult(); await holding; }
    }

    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("Network must not run while queued.");
    }
    private sealed class NoCredentials : IHttpCredentialResolver
    {
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => null;
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => null;
    }

    private static HttpOutputSettings Parse(string basis, string path) => HttpOutputSettings.Parse(JsonSerializer.SerializeToElement(new
    { connectionId = 1, baseAddress = basis, path, inputs = Inputs }));
}
