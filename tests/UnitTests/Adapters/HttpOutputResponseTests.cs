using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpOutputResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(200, OutputSendStatus.Ack, false)]
    [InlineData(204, OutputSendStatus.Ack, false)]
    [InlineData(299, OutputSendStatus.Ack, false)]
    [InlineData(301, OutputSendStatus.SuspendTarget, false)]
    [InlineData(307, OutputSendStatus.SuspendTarget, false)]
    [InlineData(400, OutputSendStatus.RejectMessage, false)]
    [InlineData(401, OutputSendStatus.SuspendTarget, false)]
    [InlineData(403, OutputSendStatus.SuspendTarget, false)]
    [InlineData(404, OutputSendStatus.SuspendTarget, false)]
    [InlineData(405, OutputSendStatus.SuspendTarget, false)]
    [InlineData(408, OutputSendStatus.Retry, false)]
    [InlineData(409, OutputSendStatus.RejectMessage, false)]
    [InlineData(413, OutputSendStatus.RejectMessage, false)]
    [InlineData(422, OutputSendStatus.RejectMessage, false)]
    [InlineData(429, OutputSendStatus.Retry, false)]
    [InlineData(500, OutputSendStatus.Retry, true)]
    [InlineData(501, OutputSendStatus.Retry, true)]
    [InlineData(502, OutputSendStatus.Retry, false)]
    [InlineData(503, OutputSendStatus.Retry, false)]
    [InlineData(504, OutputSendStatus.Retry, false)]
    [InlineData(505, OutputSendStatus.Retry, true)]
    [InlineData(600, OutputSendStatus.Retry, true)]
    public async Task StatusCategoriesAndPoisonCountsMatchTheContract(int status, OutputSendStatus expected, bool poison)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-response-do-not-log") };
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(expected, result.Status);
        Assert.Equal(poison, result.CountsTowardPoison);
        Assert.DoesNotContain("private-response-do-not-log", result.Error ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"code\":0}", "0", true)]
    [InlineData("{\"code\":0.0}", "0", true)]
    [InlineData("{\"code\":\"0\"}", "0", false)]
    [InlineData("{\"code\":null}", "null", true)]
    [InlineData("{}", "null", false)]
    [InlineData("{\"code\":9007199254740993}", "9007199254740993", true)]
    [InlineData("{\"code\":9007199254740992}", "9007199254740993", false)]
    [InlineData("{\"code\":0,\"code\":1}", "0", false)]
    [InlineData("not-json", "0", false)]
    [InlineData("", "0", false)]
    public async Task BusinessMatchIsTypedPreciseAndRequiresAnUnambiguousField(string body, string expected, bool ack)
    {
        var rule = Rule("/code", expected);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        var result = await new HttpOutputResponse(rule).EvaluateAsync(response, Now, Token);
        Assert.Equal(ack ? OutputSendStatus.Ack : OutputSendStatus.Retry, result.Status);
        Assert.Equal(!ack, result.CountsTowardPoison);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(5, 5)]
    [InlineData(3600, 60)]
    public async Task DeltaRetryAfterIsBounded(int requested, int? expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(requested));
        var result = await new HttpOutputResponse().EvaluateAsync(response, Now, Token);
        Assert.Equal(expected is { } seconds ? TimeSpan.FromSeconds(seconds) : null, result.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterDatesAndMalformedValuesDoNotCauseUnboundedDelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var evaluator = new HttpOutputResponse();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), (await evaluator.EvaluateAsync(response, Now, Token)).RetryAfter);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddDays(1));
        Assert.Equal(TimeSpan.FromSeconds(60), (await evaluator.EvaluateAsync(response, Now, Token)).RetryAfter);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(-1));
        Assert.Null((await evaluator.EvaluateAsync(response, Now, Token)).RetryAfter);
        response.Headers.Remove("Retry-After");
        response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
        Assert.Null((await evaluator.EvaluateAsync(response, Now, Token)).RetryAfter);
    }

    [Fact]
    public async Task ExplicitCancellationRemainsCancellationEvenForAckStatus()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpOutputResponse().EvaluateAsync(response, Now, cancellation.Token));
    }

    [Fact]
    public void InvalidRulesFailAtBuildAndExpectedValueDoesNotBorrowDocumentLifetime()
    {
        using var objectValue = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentException>(() => new HttpSuccessRule("/code", objectValue.RootElement));
        Assert.Throws<ArgumentException>(() => new HttpSuccessRule("not-a-path", JsonSerializer.SerializeToElement(0)));
        Assert.NotNull(Rule("/code", "0"));
    }

    [Theory]
    [InlineData(0, OutputSendStatus.Ack)]
    [InlineData(1, OutputSendStatus.Retry)]
    public async Task BusinessResponseLimitIncludesTheEntireDocument(int extra, OutputSendStatus expected)
    {
        const string json = "{\"code\":0}";
        var body = json + new string(' ', HttpOutputResponse.MaximumBodyBytes - json.Length + extra);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        var result = await new HttpOutputResponse(Rule("/code", "0")).EvaluateAsync(response, Now, Token);
        Assert.Equal(expected, result.Status);
    }

    private static HttpSuccessRule Rule(string path, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        return new HttpSuccessRule(path, document.RootElement);
    }
}
