using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpResponseSummaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpCredential Credential = new(new(HttpCredentialKind.Basic, "fixture-user", null, "account", "version"), "fixture-secret/敏感");

    [Theory]
    [InlineData(422, OutputSendStatus.RejectMessage)]
    [InlineData(403, OutputSendStatus.SuspendTarget)]
    [InlineData(429, OutputSendStatus.Retry)]
    [InlineData(500, OutputSendStatus.Retry)]
    public async Task CapturedResponseIsSeparateFromSafeErrorsAndResultLogging(int status, OutputSendStatus expected)
    {
        const string body = "{\"message\":\"device field is not accepted\",\"value\":9007199254740993}";
        using var response = Response(status, body, "application/problem+json");
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(expected, result.Status);
        var detail = Assert.IsType<HttpResponseSummary>(result.ResponseSummary);
        Assert.Equal(status, detail.StatusCode);
        Assert.Equal(HttpResponseSummaryState.Captured, detail.State);
        Assert.Equal(body, detail.Text);
        Assert.False(detail.Truncated);
        Assert.Contains($"HTTP {status}", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(body, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(body, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, detail.ToString(), StringComparison.Ordinal);
        Assert.Equal(status == 500, result.CountsTowardPoison);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("uri")]
    [InlineData("uri-lower")]
    [InlineData("basic")]
    [InlineData("json-escape")]
    [InlineData("json-key")]
    [InlineData("label")]
    public async Task ActualRequestCredentialAndSensitiveLabelsHideTheEntireResponse(string form)
    {
        var secret = form switch
        {
            "uri" => Uri.EscapeDataString(Credential.Secret),
            "uri-lower" => Uri.EscapeDataString(Credential.Secret).ToLowerInvariant(),
            "basic" => Convert.ToBase64String(Encoding.UTF8.GetBytes(Credential.Identity.Username + ":" + Credential.Secret)),
            "json-escape" => JsonSerializer.Serialize(Credential.Secret).Replace("fixture", "\\u0066ixture", StringComparison.Ordinal),
            "json-key" => "{\"access_token\":\"not-the-current-credential\"}",
            "label" => "Authorization: Bearer unrelated-private-value",
            _ => Credential.Secret,
        };
        using var response = Response(422, secret);
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token, Credential);
        Assert.Equal(OutputSendStatus.RejectMessage, result.Status);
        Assert.Equal(HttpResponseSummaryState.Redacted, result.ResponseSummary!.State);
        Assert.Null(result.ResponseSummary.Text);
        Assert.False(result.ResponseSummary.Truncated);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivacyChecksInspectTheWholeBoundedDocumentBeforeTakingTheDisplayPrefix()
    {
        using var response = Response(422, "{\"message\":\"" + new string('x', 5000) + "\",\"password\":\"secret-tail\"}", "application/json");
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(HttpResponseSummaryState.Redacted, result.ResponseSummary!.State);
        Assert.Null(result.ResponseSummary.Text);
    }

    [Fact]
    public async Task DisplayLimitUsesUtf8BytesAndDoesNotSplitASurrogatePair()
    {
        var text = string.Concat(Enumerable.Repeat("🙂", 300));
        using var response = Response(422, text);
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        var summary = result.ResponseSummary!;
        Assert.True(summary.Truncated);
        Assert.Equal(HttpResponseSummaryState.Captured, summary.State);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(summary.Text!));
        Assert.Equal(string.Concat(Enumerable.Repeat("🙂", 256)), summary.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedResponsesNeverKeepAnUnscreenedPrefixAndReadsHaveOneByteLookahead(bool knownLength)
    {
        using var stream = new ProbeStream { LengthToRead = HttpResponseSummaryCapture.MaximumBodyBytes * 2 };
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StreamContent(stream) };
        if (knownLength)
        { response.Content.Headers.ContentLength = stream.LengthToRead; }
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(HttpResponseSummaryState.TooLarge, result.ResponseSummary!.State);
        Assert.Null(result.ResponseSummary.Text);
        Assert.Equal(knownLength ? 0 : HttpResponseSummaryCapture.MaximumBodyBytes + 1, stream.ReadBytes);
    }

    [Theory]
    [InlineData("gzip", HttpResponseSummaryState.UnsupportedEncoding)]
    [InlineData("charset", HttpResponseSummaryState.UnsupportedEncoding)]
    [InlineData("binary", HttpResponseSummaryState.UnsupportedMediaType)]
    public async Task UnsupportedEncodingOrMediaDoesNotReadTheBody(string mode, HttpResponseSummaryState expected)
    {
        using var stream = new ProbeStream { Fail = true };
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) };
        if (mode == "gzip")
        { response.Content.Headers.ContentEncoding.Add("gzip"); }
        if (mode == "charset")
        { response.Content.Headers.ContentType = new("text/plain") { CharSet = "utf-16" }; }
        if (mode == "binary")
        { response.Content.Headers.ContentType = new("application/octet-stream"); }
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(expected, result.ResponseSummary!.State);
        Assert.False(stream.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData("", "text/plain", HttpResponseSummaryState.Empty)]
    [InlineData(" \r\n", "application/json", HttpResponseSummaryState.Empty)]
    [InlineData("\"\\ud800\"", "application/json", HttpResponseSummaryState.InvalidBody)]
    [InlineData("broken-json", "application/json", HttpResponseSummaryState.InvalidBody)]
    [InlineData("{broken-json", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("message\u001b[31m", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("message\u202ehidden", "text/plain", HttpResponseSummaryState.InvalidBody)]
    [InlineData("<script>alert(1)</script>", "text/html", HttpResponseSummaryState.Captured)]
    public async Task UnsupportedOrUnsafeTextIsExplicitRatherThanPretendingNoResponse(string body, string media, HttpResponseSummaryState expected)
    {
        using var response = Response(422, body, media);
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(expected, result.ResponseSummary!.State);
        if (expected != HttpResponseSummaryState.Captured)
        { Assert.Null(result.ResponseSummary.Text); }
    }

    [Fact]
    public async Task InvalidUtf8IsNeverReplacedWithApparentlySafeText()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new ByteArrayContent([0xc3, 0x28]) };
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(HttpResponseSummaryState.UnsupportedEncoding, result.ResponseSummary!.State);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("caller")]
    [InlineData("disconnect")]
    public async Task OptionalCaptureFailureCannotChangeAnAlreadyKnownHttpClassification(string mode)
    {
        var clock = new FakeTimeProvider();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new ProbeStream { Wait = mode != "disconnect", Fail = mode == "disconnect" };
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(stream) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));
        var evaluating = new HttpOutputResponse(time: clock).EvaluateAsync(response, Now, caller.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (mode == "deadline")
        { clock.Advance(HttpResponseSummaryCapture.CaptureTimeout); }
        if (mode == "caller")
        { await caller.CancelAsync(); }
        var result = await evaluating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.False(result.CountsTowardPoison);
        Assert.Equal(TimeSpan.FromSeconds(15), result.RetryAfter);
        Assert.Equal(HttpResponseSummaryState.Unavailable, result.ResponseSummary!.State);
        Assert.Null(result.ResponseSummary.Text);
    }

    [Fact]
    public async Task AckWithoutBusinessRuleNeverReadsOrRetainsResponseAndEntryCancellationStillThrows()
    {
        using var stream = new ProbeStream { Fail = true };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(OutputSendStatus.Ack, result.Status);
        Assert.Null(result.ResponseSummary);
        Assert.False(stream.Entered.Task.IsCompleted);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpOutputResponse().EvaluateAsync(response, Now, cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessMismatchUsesTheSameCompleteBodyWithoutLeakingCredential(bool sensitive)
    {
        var body = JsonSerializer.Serialize(new { code = 1, message = sensitive ? Credential.Secret : "field is unavailable" });
        using var response = Response(200, body, "application/json");
        var rule = new HttpSuccessRule("/code", JsonSerializer.SerializeToElement(0));
        var result = await new HttpOutputResponse(rule).EvaluateAsync(response, Now, Token, Credential);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.True(result.CountsTowardPoison);
        Assert.Equal(sensitive ? HttpResponseSummaryState.Redacted : HttpResponseSummaryState.Captured, result.ResponseSummary!.State);
        Assert.Equal(sensitive ? null : body, result.ResponseSummary.Text);
    }

    [Theory]
    [InlineData("invalid", HttpResponseSummaryState.InvalidBody, true)]
    [InlineData("large", HttpResponseSummaryState.TooLarge, true)]
    [InlineData("disconnect", HttpResponseSummaryState.Unavailable, false)]
    public async Task UnusableBusinessResponseStillReportsHttpStatusWithoutChangingPoisonClassification(string scenario, HttpResponseSummaryState state, bool poison)
    {
        using var response = scenario == "disconnect"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ProbeStream { Fail = true }) }
            : Response(200, scenario == "large" ? new string('x', 65537) : "broken-json", "application/json");
        var result = await new HttpOutputResponse(new HttpSuccessRule("/code", JsonSerializer.SerializeToElement(0))).EvaluateAsync(response, Now, Token);
        Assert.Equal(OutputSendStatus.Retry, result.Status);
        Assert.Equal(poison, result.CountsTowardPoison);
        Assert.Equal(state, result.ResponseSummary!.State);
        Assert.Equal(200, result.ResponseSummary.StatusCode);
        Assert.Null(result.ResponseSummary.Text);
        Assert.Contains("HTTP 200", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryIsImmutableBoundedAndRoundTripsWithoutMakingToStringADataLeak()
    {
        Assert.Throws<ArgumentException>(() => new HttpResponseSummary(422, HttpResponseSummaryState.Captured));
        Assert.Throws<ArgumentException>(() => new HttpResponseSummary(422, HttpResponseSummaryState.Redacted, "private"));
        Assert.Throws<ArgumentException>(() => new HttpResponseSummary(422, HttpResponseSummaryState.Empty, truncated: true));
        Assert.Throws<ArgumentException>(() => new HttpResponseSummary(422, HttpResponseSummaryState.Captured, new string('x', 1025)));
        Assert.Throws<ArgumentException>(() => new HttpResponseSummary(422, (HttpResponseSummaryState)100));
        var detail = new HttpResponseSummary(422, HttpResponseSummaryState.Captured, "private-text", true);
        var json = JsonSerializer.Serialize(detail);
        var restored = JsonSerializer.Deserialize<HttpResponseSummary>(json)!;
        Assert.Equal(detail.Text, restored.Text);
        Assert.True(restored.Truncated);
        Assert.DoesNotContain(detail.Text!, restored.ToString(), StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new HttpResponseSummary(422, HttpResponseSummaryState.Captured, new string('\n', 1024)))) < HttpResponseSummary.MaximumSerializedBytes);
    }

    private static HttpResponseMessage Response(int status, string body, string media = "text/plain") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) };

    private sealed class ProbeStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LengthToRead { get; init; }
        public int ReadBytes { get; private set; }
        public bool Fail { get; init; }
        public bool Wait { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (Wait)
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            if (Fail)
            { throw new IOException("private transport details"); }
            var count = Math.Min(buffer.Length, LengthToRead - ReadBytes);
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
