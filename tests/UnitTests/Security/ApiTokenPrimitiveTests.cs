using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;

namespace TenonEdge.UnitTests.Security;

public sealed class ApiTokenPrimitiveTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(9007199254740991L)]
    public void GeneratedSecretHasFullRandomPayloadAndParsesItsPublicIdentifier(long id)
    {
        var secret = ApiTokenSecrets.Create(id);
        Assert.True(ApiTokenSecrets.TryId(secret, out var actual));
        Assert.Equal(id, actual);
        var encoded = secret.Split('.')[2];
        Assert.Equal(43, encoded.Length);
        Assert.Equal(32, Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.NotEqual(secret, ApiTokenSecrets.Create(id));
        Assert.Equal(32, ApiTokenSecrets.Hash(secret).Length);
        Assert.False(ApiTokenSecrets.TryId(secret + " ", out _));
        Assert.False(ApiTokenSecrets.TryId("te1.0" + secret[4..], out _));
        Assert.False(ApiTokenSecrets.TryId(secret.Replace("te1.", "te2.", StringComparison.Ordinal), out _));
    }
    [Fact]
    public void OutstandingAttemptsReserveTheFailureBudgetAndSuccessReleasesWithoutCounting()
    {
        var clock = new RateClock();
        var failures = new ApiTokenFailures(clock);
        var pending = Enumerable.Range(0, 10).Select(_ => Assert.IsType<ApiTokenFailures.Attempt>(failures.TryEnter("source", out _))).ToArray();
        Assert.Null(failures.TryEnter("source", out var delay));
        Assert.Equal(60, delay);
        foreach (var attempt in pending)
        { attempt.Dispose(); attempt.Dispose(); }
        using (var success = failures.TryEnter("source", out delay))
        { Assert.NotNull(success); Assert.Equal(0, delay); }
        for (var i = 0; i < 10; i++)
        {
            using var failure = failures.TryEnter("source", out _);
            Assert.NotNull(failure);
            failure.Failed = true;
        }
        Assert.Null(failures.TryEnter("source", out _));
        using var independent = failures.TryEnter("another", out _);
        Assert.NotNull(independent);
        clock.UtcOffset = TimeSpan.FromDays(2); // UTC alone is not the rate window.
        Assert.Null(failures.TryEnter("source", out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var nextWindow = failures.TryEnter("source", out _);
        Assert.NotNull(nextWindow);
    }
    [Fact]
    public void NewSourcesCannotEvictFailuresOrMakeTheRegistryUnbounded()
    {
        var clock = new FakeTimeProvider();
        var failures = new ApiTokenFailures(clock);
        for (var i = 0; i < ApiTokenFailures.MaximumSources; i++)
        {
            using var attempt = failures.TryEnter("source-" + i, out _);
            Assert.NotNull(attempt);
            attempt.Failed = true;
        }
        Assert.Null(failures.TryEnter("extra-source", out var delay));
        Assert.Equal(60, delay);
        using (var existing = failures.TryEnter("source-0", out _))
        { Assert.NotNull(existing); }
        clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = failures.TryEnter("extra-source", out _);
        Assert.NotNull(recovered);
    }

    private sealed class RateClock : TimeProvider
    {
        private long _ticks;
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + UtcOffset;
        public void Advance(TimeSpan value) => _ticks += value.Ticks;
    }
}
