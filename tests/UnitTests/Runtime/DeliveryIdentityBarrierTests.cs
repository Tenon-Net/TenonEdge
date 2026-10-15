using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.UnitTests.Runtime;

public sealed class DeliveryIdentityBarrierTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangeDrainsExistingPublicationsAndBlocksOnlyItsConnection()
    {
        var barrier = new DeliveryIdentityBarrier();
        using var first = await barrier.EnterPublicationAsync([1, 1, 2], Token);
        using var second = await barrier.EnterPublicationAsync([1], Token);
        var change = barrier.EnterChangeAsync(1, Token).AsTask();
        Assert.False(change.IsCompleted);
        var next = barrier.EnterPublicationAsync([1, 2], Token).AsTask();
        Assert.False(next.IsCompleted);
        using var unrelated = await barrier.EnterPublicationAsync([2, 3], Token);
        first.Dispose();
        Assert.False(change.IsCompleted);
        second.Dispose();
        using var writing = await change.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(next.IsCompleted);
        writing.Dispose();
        using var resumed = await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task CancelledChangeUnblocksPublicationsButDoesNotForgetEarlierPublication()
    {
        var barrier = new DeliveryIdentityBarrier();
        using var first = await barrier.EnterPublicationAsync([1], Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var change = barrier.EnterChangeAsync(1, stop.Token).AsTask();
        var next = barrier.EnterPublicationAsync([1], Token).AsTask();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        using var resumed = await next.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var retry = barrier.EnterChangeAsync(1, Token).AsTask();
        resumed.Dispose();
        Assert.False(retry.IsCompleted);
        first.Dispose();
        using var writing = await retry.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task MultiTargetPublicationDoesNotReserveSomeTargetsWhileWaitingForOthers()
    {
        var barrier = new DeliveryIdentityBarrier();
        using var change = await barrier.EnterChangeAsync(1, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = barrier.EnterPublicationAsync([2, 1, 2], stop.Token).AsTask();
        using var otherChange = await barrier.EnterChangeAsync(2, Token);
        Assert.False(pending.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        change.Dispose();
        otherChange.Dispose();
        using var recovered = await barrier.EnterPublicationAsync([1, 2], Token);
    }

    [Fact]
    public async Task ConcurrentChangesRemainExclusiveAndLeasesReleaseOnlyOnce()
    {
        var barrier = new DeliveryIdentityBarrier();
        using var first = await barrier.EnterChangeAsync(1, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = barrier.EnterChangeAsync(1, stop.Token).AsTask();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var second = barrier.EnterChangeAsync(1, Token).AsTask();
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var writing = await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        first.Dispose();
        var publication = barrier.EnterPublicationAsync([1], Token).AsTask();
        Assert.False(publication.IsCompleted);
        writing.Dispose();
        using var resumed = await publication.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }
}
