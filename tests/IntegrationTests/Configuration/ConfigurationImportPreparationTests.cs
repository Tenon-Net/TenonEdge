using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Configuration;

public sealed class ConfigurationImportPreparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Owner = "cookie:fixture-session";
    private static MemoryStream File(string? json = null) => new(Encoding.UTF8.GetBytes(json ?? """
        {"formatVersion":1,"connections":[],"devices":[],"inputs":[],"routes":[]}
        """));
    private static ConfigurationImportPreparation Preparation(IServiceProvider services, TimeProvider? time = null)
    {
        TestAccounts.Create(services, "fixture", ApiRequests.StrongPassword, UserRole.Configurator);
        return new(ActivatorUtilities.CreateInstance<ConfigurationImportStore>(services), time ?? TimeProvider.System);
    }
    private static ManagementActor Actor(IServiceProvider services)
    {
        var user = TestAccounts.Find(services, "fixture")!;
        return new(user.Id, user.SecurityStamp, null);
    }
    private static string Version(IServiceProvider services) => services.GetRequiredService<ConfigDatabase>().InTransaction(ConfigurationDraft.ReadVersion);

    [Fact]
    public async Task OwnerMustExplicitlyConfirmAndSuccessfulConfirmationCannotBeReplayed()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var stream = File();
        var review = await service.PrepareAsync(stream, ConfigurationImportMode.Replace, "0", Owner, Token);
        Assert.Equal("0", Version(host.Services));
        Assert.Null(service.Current("different-session"));
        Assert.Equal(review, service.Current(Owner));
        Assert.Throws<ArgumentException>(() => service.Confirm(review.Id, false, Owner, Actor(host.Services), Token));
        Assert.Throws<ConfigurationImportRequestException>(() => service.Cancel(review.Id, "different-session"));
        Assert.Equal(404, Assert.Throws<ConfigurationImportRequestException>(() =>
            service.Confirm(review.Id, true, "different-session", Actor(host.Services), Token)).Status);
        var result = service.Confirm(review.Id, true, Owner, Actor(host.Services), Token);
        Assert.Equal(result.Version, Version(host.Services));
        Assert.NotEqual("0", result.Version);
        Assert.Null(service.Current(Owner));
        Assert.Equal(404, Assert.Throws<ConfigurationImportRequestException>(() =>
            service.Confirm(review.Id, true, Owner, Actor(host.Services), Token)).Status);
        Assert.Equal(result.Version, Version(host.Services));
    }

    [Fact]
    public async Task OtherOwnerCannotUploadUntilCancelledAndDeniedBodyIsNotRead()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var stream = File();
        var review = await service.PrepareAsync(stream, ConfigurationImportMode.Merge, "0", Owner, Token);
        using var denied = new ReadProbeStream();
        Assert.Equal(409, (await Assert.ThrowsAsync<ConfigurationImportRequestException>(() =>
            service.PrepareAsync(denied, ConfigurationImportMode.Merge, "0", "other-session", Token))).Status);
        Assert.False(denied.ReadAttempted);
        service.Cancel(review.Id, Owner);
        using var next = File();
        Assert.NotEqual(review.Id, (await service.PrepareAsync(next, ConfigurationImportMode.Merge, "0", "other-session", Token)).Id);
        Assert.Equal("0", Version(host.Services));
    }

    [Fact]
    public async Task MonotonicExpiryIgnoresWallClockJumpsAndReleasesPreparation()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var time = new WallClockProvider();
        var service = Preparation(host.Services, time);
        using var stream = File();
        var review = await service.PrepareAsync(stream, ConfigurationImportMode.Merge, "0", Owner, Token);
        time.Offset = TimeSpan.FromDays(-1);
        Assert.Equal(review, service.Current(Owner));
        time.Inner.Advance(ConfigurationImportPreparation.Lifetime);
        Assert.Null(service.Current(Owner));
        Assert.Throws<ConfigurationImportRequestException>(() => service.Confirm(review.Id, true, Owner, Actor(host.Services), Token));
        Assert.Equal("0", Version(host.Services));
    }

    [Fact]
    public async Task FailedReplacementUploadInvalidatesPriorConfirmationWithoutWritingDraft()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var stream = File();
        var review = await service.PrepareAsync(stream, ConfigurationImportMode.Merge, "0", Owner, Token);
        using var invalid = File("{");
        await Assert.ThrowsAsync<ConfigurationImportException>(() => service.PrepareAsync(invalid, ConfigurationImportMode.Merge, "0", Owner, Token));
        Assert.Null(service.Current(Owner));
        Assert.Throws<ConfigurationImportRequestException>(() => service.Confirm(review.Id, true, Owner, Actor(host.Services), Token));
        Assert.Equal("0", Version(host.Services));
    }

    [Fact]
    public async Task ConfirmationConflictConsumesReviewInsteadOfAllowingReplay()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var stream = File();
        var review = await service.PrepareAsync(stream, ConfigurationImportMode.Replace, "0", Owner, Token);
        var changed = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "newer", "Newer", ConnectionKind.Http, Http: new("https://example.com/")), "fixture", null);
        Assert.Throws<DraftWriteException>(() => service.Confirm(review.Id, true, Owner, Actor(host.Services), Token));
        Assert.Null(service.Current(Owner));
        Assert.Throws<ConfigurationImportRequestException>(() => service.Confirm(review.Id, true, Owner, Actor(host.Services), Token));
        Assert.Equal(changed.Version, Version(host.Services));
    }

    [Fact]
    public async Task ConcurrentPreparationIsRejectedBeforeReadingSecondBody()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var held = new ReadProbeStream(hold: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = service.PrepareAsync(held, ConfigurationImportMode.Merge, "0", Owner, cts.Token);
        await held.Started.Task.WaitAsync(Token);
        using var second = new ReadProbeStream();
        Assert.Equal(409, (await Assert.ThrowsAsync<ConfigurationImportRequestException>(() =>
            service.PrepareAsync(second, ConfigurationImportMode.Merge, "0", Owner, Token))).Status);
        Assert.False(second.ReadAttempted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        using var next = File();
        Assert.NotNull(await service.PrepareAsync(next, ConfigurationImportMode.Merge, "0", Owner, Token));
        Assert.Equal("0", Version(host.Services));
    }

    [Fact]
    public async Task OversizedUploadReadsOnlyLimitPlusOneAndNeverLeavesConfirmation()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var service = Preparation(host.Services);
        using var stream = new MemoryStream(new byte[ConfigurationExport.MaximumBytes + 1024]);
        var error = await Assert.ThrowsAsync<ConfigurationImportException>(() => service.PrepareAsync(stream, ConfigurationImportMode.Merge, "0", Owner, Token));
        Assert.Equal("import.fileTooLarge", Assert.Single(error.Issues).Code);
        Assert.Equal(ConfigurationExport.MaximumBytes + 1L, stream.Position);
        Assert.Null(service.Current(Owner));
        Assert.Equal("0", Version(host.Services));
    }

    [Fact]
    public async Task SlowUploadHasDeadlineAndReleasesSinglePreparationSlot()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var time = new FakeTimeProvider();
        var service = Preparation(host.Services, time);
        using var held = new ReadProbeStream(hold: true);
        var upload = service.PrepareAsync(held, ConfigurationImportMode.Merge, "0", Owner, Token);
        await held.Started.Task.WaitAsync(Token);
        time.Advance(ConfigurationImportPreparation.UploadTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Null(service.Current(Owner));
        using var next = File();
        Assert.NotNull(await service.PrepareAsync(next, ConfigurationImportMode.Merge, "0", Owner, Token));
        Assert.Equal("0", Version(host.Services));
    }

    // FakeTimeProvider.AdjustTime also changes its GetTimestamp value. Split the
    // wall-clock offset from the monotonic clock instead of testing a fake rollback.
    private sealed class WallClockProvider : TimeProvider
    {
        public FakeTimeProvider Inner { get; } = new();
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow() + Offset;
        public override long GetTimestamp() => Inner.GetTimestamp();
        public override long TimestampFrequency => Inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class ReadProbeStream(bool hold = false) : MemoryStream
    {
        public bool ReadAttempted { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            Started.TrySetResult();
            if (hold)
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            throw new InvalidOperationException("The rejected request body must not be read.");
        }
    }
}
