using System.Net;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpTestResponseCaptureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly HttpCredential Credential = new(new(HttpCredentialKind.Basic, "fixture-user", null, "account", "v1"), "fixture-secret/敏感");

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(503)]
    public async Task KeepsComplete64KiBTextAndStatusWithoutParsingNumbersOrLoggingBody(int status)
    {
        const string prefix = "{\"value\":9223372036854775807,\"decimal\":1.000000000000000001,\"message\":\"";
        var body = prefix + new string('x', HttpTestResponseCapture.MaximumBodyBytes - prefix.Length - 2) + "\"}";
        using var response = Response(status, body, "application/json");
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal("http.response", result.Code);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(HttpResponseSummaryState.Captured, result.BodyState);
        Assert.Equal(body, result.Body);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("9223372036854775807", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverLimitDoesNotExposeAnUninspectedPrefixAndHasBoundedReads(bool knownLength)
    {
        using var stream = new ProbeStream { Size = HttpTestResponseCapture.MaximumBodyBytes * 2 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (knownLength)
        { response.Content.Headers.ContentLength = stream.Size; }
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal(HttpResponseSummaryState.TooLarge, result.BodyState);
        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Truncated);
        Assert.Null(result.Body);
        Assert.Equal(knownLength ? 0 : HttpTestResponseCapture.MaximumBodyBytes, stream.ReadBytes);
    }

    [Theory]
    [InlineData(65535, HttpResponseSummaryState.Captured)]
    [InlineData(65536, HttpResponseSummaryState.TooLarge)]
    public async Task UnknownLengthAtTheExactLimitIsConservativelyHiddenRatherThanReadingAnotherByte(int size, HttpResponseSummaryState state)
    {
        using var stream = new ProbeStream { Size = size };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal(state, result.BodyState);
        Assert.Equal(size, stream.ReadBytes);
        Assert.Equal(state == HttpResponseSummaryState.TooLarge, result.Truncated);
        Assert.Equal(state == HttpResponseSummaryState.Captured ? new string('x', size) : null, result.Body);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("uri")]
    [InlineData("basic")]
    [InlineData("escaped")]
    [InlineData("field")]
    [InlineData("label")]
    [InlineData("tail")]
    public async Task SharesFormalPrivacyRulesForCompleteBodiesIncludingDecodedAndLateSecrets(string scenario)
    {
        var body = scenario switch
        {
            "uri" => Uri.EscapeDataString(Credential.Secret).ToLowerInvariant(),
            "basic" => Convert.ToBase64String(Encoding.UTF8.GetBytes(Credential.Identity.Username + ":" + Credential.Secret)),
            "escaped" => JsonSerializer.Serialize(Credential.Secret).Replace("fixture", "\\u0066ixture", StringComparison.Ordinal),
            "field" => "{\"access_\\u0074oken\":\"unrelated-private-value\"}",
            "label" => "Authorization: Bearer unrelated-private-value",
            "tail" => "{\"message\":\"" + new string('x', 60000) + "\",\"password\":\"unrelated-private-value\"}",
            _ => Credential.Secret,
        };
        using var response = Response(200, body);
        var result = await HttpTestResponseCapture.ReadAsync(response, Credential, Token);
        Assert.Equal(HttpResponseSummaryState.Redacted, result.BodyState);
        Assert.Null(result.Body);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "text/plain", HttpResponseSummaryState.Empty)]
    [InlineData("broken-json", "application/json", HttpResponseSummaryState.InvalidBody)]
    [InlineData("{broken", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("\"\\ud800\"", "application/json", HttpResponseSummaryState.InvalidBody)]
    [InlineData("escape\u001b[31m", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("bidi\u202eend", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("<script>alert(1)</script>", "text/html", HttpResponseSummaryState.Captured)]
    public async Task UnusableBodiesAreExplicitAndHtmlRemainsOnlyText(string body, string media, HttpResponseSummaryState state)
    {
        using var response = Response(422, body, media);
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal(state, result.BodyState);
        Assert.Equal(state == HttpResponseSummaryState.Captured ? body : null, result.Body);
    }

    [Theory]
    [InlineData("binary", HttpResponseSummaryState.UnsupportedMediaType)]
    [InlineData("gzip", HttpResponseSummaryState.UnsupportedEncoding)]
    [InlineData("charset", HttpResponseSummaryState.UnsupportedEncoding)]
    public async Task UnsupportedEncodingAndBinaryNeverReadOrDecompress(string scenario, HttpResponseSummaryState state)
    {
        using var stream = new ProbeStream { Fail = true };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (scenario == "binary")
        { response.Content.Headers.ContentType = new("application/octet-stream"); }
        if (scenario == "charset")
        { response.Content.Headers.ContentType = new("text/plain") { CharSet = "utf-16" }; }
        if (scenario == "gzip")
        { response.Content.Headers.ContentEncoding.Add("gzip"); }
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal(state, result.BodyState);
        Assert.False(stream.Entered.Task.IsCompleted);
        Assert.Null(result.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenOrCanceledBodyPreservesTheAlreadyReceivedStatus(bool canceled)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new ProbeStream { Fail = !canceled, Wait = canceled };
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(stream) };
        var pending = HttpTestResponseCapture.ReadAsync(response, null, stop.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (canceled)
        { await stop.CancelAsync(); }
        var result = await pending;
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(HttpResponseSummaryState.Unavailable, result.BodyState);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task InvalidUtf8DoesNotGetReplacementCharactersThatHideTheFailure()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xc3, 0x28]) };
        var result = await HttpTestResponseCapture.ReadAsync(response, null, Token);
        Assert.Equal(HttpResponseSummaryState.UnsupportedEncoding, result.BodyState);
        Assert.Null(result.Body);
    }

    private static HttpResponseMessage Response(int status, string body, string media = "text/plain") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) };

    private sealed class ProbeStream : Stream
    {
        public int Size { get; init; }
        public int ReadBytes { get; private set; }
        public bool Fail { get; init; }
        public bool Wait { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (Wait)
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            if (Fail)
            { throw new IOException("private wire details"); }
            var count = Math.Min(buffer.Length, Size - ReadBytes);
            buffer.Span[..count].Fill((byte)'x');
            ReadBytes += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
