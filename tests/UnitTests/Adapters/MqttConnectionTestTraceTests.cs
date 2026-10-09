using System.Net.Security;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttConnectionTestTraceTests
{
    [Fact]
    public void TimingsUseMonotonicBoundariesAndAuthenticationSharesConnackInterval()
    {
        var time = new FakeTimeProvider();
        var trace = new ConnectionTestTrace(time, true);
        trace.Begin("address");
        time.Advance(TimeSpan.FromMilliseconds(5));
        trace.Begin("connection");
        time.Advance(TimeSpan.FromMilliseconds(7));
        trace.TransportConnected();
        time.Advance(TimeSpan.FromMilliseconds(11));
        trace.Begin("protocol");
        time.Advance(TimeSpan.FromMilliseconds(13));
        var steps = trace.FinishMqtt("success");
        Assert.Equal(new double?[] { 5, 7, 11, 13, 13 }, steps.Select(step => step.ElapsedMs));
        Assert.All(steps, step => Assert.Equal("success", step.Status));
        Assert.Null(steps[2].TimingGroup);
        Assert.Equal("mqtt.connack", steps[3].TimingGroup);
        Assert.Equal(steps[3].TimingGroup, steps[4].TimingGroup);
        // 完成后的迟到回调不改已冻结的结果。
        trace.Begin("address");
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(steps, trace.FinishMqtt("timeout"));
    }

    [Fact]
    public void EarlyFailureLeavesLaterPhasesUnreachedAndDisabledTlsSkipped()
    {
        var trace = new ConnectionTestTrace(new FakeTimeProvider(), false);
        trace.Begin("address");
        var steps = trace.FinishMqtt("dns.failed");
        Assert.Equal("failed", steps[0].Status);
        Assert.Equal("dns.failed", steps[0].Code);
        Assert.Equal("notReached", steps[1].Status);
        Assert.Equal("skipped", steps[2].Status);
        Assert.Equal("notReached", steps[3].Status);
        Assert.Equal("notReached", steps[4].Status);
        Assert.All(steps.Skip(1), step => Assert.Null(step.ElapsedMs));
    }

    [Fact]
    public void FailureAfterTransportDoesNotRewriteObservedSuccessfulAddressOrConnection()
    {
        var trace = new ConnectionTestTrace(new FakeTimeProvider(), false);
        trace.Begin("address");
        trace.Begin("connection");
        trace.TransportConnected();
        var steps = trace.FinishMqtt("protocol.invalid");
        Assert.Equal("success", steps[0].Status);
        Assert.Equal("success", steps[1].Status);
        Assert.Equal("unknown", steps[3].Status);
        Assert.Equal("failed", steps[4].Status);
        Assert.Null(steps[4].ElapsedMs);
    }

    [Fact]
    public void CertificateExplanationDoesNotInventErrorsForPlatformAcceptedCertificate()
    {
        var now = DateTime.UtcNow;
        Assert.Null(CertificateFailure.Classify(SslPolicyErrors.None, null, now));
        Assert.Equal("certificate.missing", CertificateFailure.Classify(SslPolicyErrors.RemoteCertificateNotAvailable, null, now));
        Assert.Equal("certificate.nameMismatch", CertificateFailure.Classify(SslPolicyErrors.RemoteCertificateNameMismatch
            | SslPolicyErrors.RemoteCertificateChainErrors, null, now));
        Assert.Equal("certificate.invalid", CertificateFailure.Classify(SslPolicyErrors.RemoteCertificateChainErrors, null, now));
    }

    [Theory]
    [InlineData("credential.rejected", "failed")]
    [InlineData("timeout", "unknown")]
    [InlineData("broker.unavailable", "unknown")]
    public void AuthenticationIsNotClaimedSuccessfulWithoutPositiveConnack(string code, string expected)
    {
        var trace = new ConnectionTestTrace(new FakeTimeProvider(), false);
        trace.TransportConnected();
        trace.Begin("protocol");
        var steps = trace.FinishMqtt(code);
        Assert.Equal("unknown", steps[0].Status);
        Assert.Null(steps[0].ElapsedMs);
        Assert.Equal(expected, steps[3].Status);
        Assert.Equal("failed", steps[4].Status);
    }
}
