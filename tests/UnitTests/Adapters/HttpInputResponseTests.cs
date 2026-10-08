using System.Net;
using System.Text;
using System.Text.Json;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpInputResponseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{\"n\":9223372036854775807}")]
    [InlineData("[true,null,\"text\"]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("  {\"value\":1} \r\n")]
    public async Task ReadsExactlyOneDocumentWithoutNumericCoercion(string json)
    {
        using var response = Response(json);
        using var document = await HttpInputResponse.ReadAsync(response, Token);
        using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, document.RootElement));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task NonSuccessStatusNeverReadsTheBody(int status)
    {
        using var stream = new ProbeStream(10);
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
        Assert.Equal(HttpInputFailure.Status, error.Failure);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"secret-value\":invalid}")]
    [InlineData("{} {}")]
    [InlineData("{} trailing-secret-value")]
    [InlineData("[1,]")]
    [InlineData("/*secret-value*/{}")]
    public async Task InvalidJsonDoesNotEchoRawContent(string json)
    {
        using var response = Response(json);
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
        Assert.Equal(HttpInputFailure.InvalidJson, error.Failure);
        Assert.DoesNotContain("secret-value", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InvalidUtf8IsRejectedAtTheResponseBoundary()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([0x22, 0xff, 0x22]) };
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
        Assert.Equal(HttpInputFailure.InvalidJson, error.Failure);
    }

    [Fact]
    public async Task DepthLimitIsExplicit()
    {
        using var allowed = Response(new string('[', 64) + "0" + new string(']', 64));
        using var document = await HttpInputResponse.ReadAsync(allowed, Token);
        using var refused = Response(new string('[', 65) + "0" + new string(']', 65));
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(refused, Token));
        Assert.Equal(HttpInputFailure.InvalidJson, error.Failure);
    }

    [Fact]
    public async Task ExactByteLimitIsAcceptedAndDocumentOwnsItsMemory()
    {
        var json = "\"" + new string('a', HttpInputResponse.MaximumBodyBytes - 2) + "\"";
        using var response = Response(json);
        using var document = await HttpInputResponse.ReadAsync(response, Token);
        using var second = Response(json.Replace('a', 'b'));
        using var overwritten = await HttpInputResponse.ReadAsync(second, Token);
        Assert.Equal(new string('a', HttpInputResponse.MaximumBodyBytes - 2), document.RootElement.GetString());
        Assert.Equal(new string('b', HttpInputResponse.MaximumBodyBytes - 2), overwritten.RootElement.GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBodyIsBoundedWithOnlyOneByteLookahead(bool knownLength)
    {
        using var stream = new ProbeStream(HttpInputResponse.MaximumBodyBytes * 4);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (knownLength)
        { response.Content.Headers.ContentLength = HttpInputResponse.MaximumBodyBytes + 1; }
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
        Assert.Equal(HttpInputFailure.TooLarge, error.Failure);
        Assert.Equal(knownLength ? 0 : HttpInputResponse.MaximumBodyBytes + 1, stream.BytesRead);
    }

    [Fact]
    public async Task UnsupportedEncodingIsRejectedBeforeReading()
    {
        using var stream = new ProbeStream(10);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentEncoding.Add("gzip");
        var error = await Assert.ThrowsAsync<HttpInputResponseException>(() => HttpInputResponse.ReadAsync(response, Token));
        Assert.Equal(HttpInputFailure.Encoding, error.Failure);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task CancellationAndNetworkFailureAreNotDisguisedAsBadJson()
    {
        using var stream = new ProbeStream(0) { Fail = true };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        await Assert.ThrowsAsync<IOException>(() => HttpInputResponse.ReadAsync(response, Token));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var other = Response("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpInputResponse.ReadAsync(other, cancelled.Token));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) };

    private sealed class ProbeStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Fail { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            { throw new IOException("test transport fault"); }
            var count = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
