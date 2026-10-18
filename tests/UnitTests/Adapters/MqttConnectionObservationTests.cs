using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttConnectionObservationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActivationBarrierAndCancelledWaitDoNotClaimConnectionSuccessOrFailure()
    {
        var connector = new WaitingConnector();
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new MqttSession(Settings(), "fixture", new NoCredentials(), TimeProvider.System, connector,
            waitForActivation: token => activation.Task.WaitAsync(token));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = session.MaintainAsync(stop.Token, stop.Token);
        Assert.False(operation.IsCompleted);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(connector.Entered.Task.IsCompleted);
        Assert.Null(session.Observation.LastErrorAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionAttemptDeadlineIsNotConfusedWithStopping(bool timeout)
    {
        var time = new FakeTimeProvider();
        var connector = new WaitingConnector();
        await using var session = new MqttSession(Settings(), "fixture", new NoCredentials(), time, connector);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var pending = session.MaintainAsync(operation.Token, stop.Token);
        await connector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(1));
        if (timeout)
        { await operation.CancelAsync(); }
        else
        { await stop.CancelAsync(); }
        try
        { Assert.NotEqual(OutputSendStatus.Ack, (await pending).Status); }
        catch (OperationCanceledException) { }
        Assert.Equal(timeout ? ConnectionCommunicationState.Degraded : ConnectionCommunicationState.Connecting, session.Observation.State);
        Assert.Equal(timeout ? "mqtt.timeout" : null, session.Observation.LastErrorCode);
        Assert.Equal(timeout ? time.GetUtcNow() : (DateTimeOffset?)null, session.Observation.LastErrorAt);
    }

    private static MqttConnectionSettings Settings() => new() { ConnectionId = 1, Host = "fixture.invalid", Port = 1883 };
    private sealed class NoCredentials : ICredentialResolver
    {
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
    }
    private sealed class WaitingConnector : IOutboundConnector
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
