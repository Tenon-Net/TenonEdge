using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class CredentialPublicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenButNotYetQueuedMessageRequiresAccountDeclaration(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (cache.GetStatus().Store is null)
        { await Task.Delay(10, timeout.Token); }
        var httpStore = factory.Services.GetRequiredService<HttpCredentialStore>();
        var mqttStore = factory.Services.GetRequiredService<MqttCredentialStore>();
        const string httpRecipient = "http://localhost:8883/";
        const string mqttRecipient = "mqtt://localhost:1883";
        var version = mqtt
            ? (await mqttStore.WriteAsync(1, new(mqttRecipient, "account", "original"), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version
            : (await httpStore.WriteAsync(1, new(httpRecipient, HttpCredentialKind.Bearer, Secret: "original"), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version;
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var target = new FrozenTarget(() => mqtt ? mqttStore.DescribeMqtt(1)!.AccountMarker : httpStore.DescribeHttp(1)!.AccountMarker, frozen, release);
        var input = Channel.CreateUnbounded<EdgeMessage>();
        var state = new InputDispatcherState();
        var dispatcher = new InputDispatcherUnit(RuntimeUnitId.Create("input.simulator", "device/input"), input.Reader,
            [target], state, 1, cache, TimeProvider.System, new RecordingEventSink());
        var now = DateTimeOffset.UtcNow;
        await input.Writer.WriteAsync(new(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now, []), timeout.Token);
        input.Writer.Complete();
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => dispatcher.RunAsync(stop.Token), Token);
        Task<Exception?>? writing = null;
        try
        {
            Assert.True(frozen.Wait(TimeSpan.FromSeconds(5), Token));
            Assert.Empty(cache.ReadPending(1, 0, 1));
            writing = Record.ExceptionAsync(async () =>
            {
                if (mqtt)
                { await mqttStore.WriteAsync(1, new(mqttRecipient, "account", "replacement", ExpectedVersion: version), "fixture", null, true, token: TestContext.Current.CancellationToken); }
                else
                { await httpStore.WriteAsync(1, new(httpRecipient, HttpCredentialKind.Bearer, Secret: "replacement", ExpectedVersion: version), "fixture", null, true, token: TestContext.Current.CancellationToken); }
            }).AsTask();
            // 原实现会在此空队列窗口成功换凭证；修复应先等冻结发布交给唯一写者。
            Assert.False(writing.IsCompleted);
            release.Set();
            var exception = await writing.WaitAsync(timeout.Token);
            Assert.Equal("credential.accountDeclarationRequired", Assert.IsType<CredentialWriteException>(exception).Code);
            await state.Drained.Task.WaitAsync(timeout.Token);
            var stored = Assert.Single(cache.ReadPending(1, 0, 10));
            Assert.Equal(target.Frozen!.Fingerprint, stored.Fingerprint);
            Assert.Equal(target.Frozen.Payload.ToArray(), stored.Payload.ToArray());
            Assert.Equal(version, mqtt ? mqttStore.Read(1)!.Version : httpStore.Read(1)!.Version);
        }
        finally
        {
            release.Set();
            if (writing is not null)
            { await writing; }
            await stop.CancelAsync();
            try
            { await running; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCheckAndCredentialCommitExcludeNewFreezing(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await ReadyAsync(cache);
        using var protecting = new ManualResetEventSlim();
        using var commit = new ManualResetEventSlim();
        var armed = false;
        var provider = new GatedProtection(factory.Services.GetRequiredService<IDataProtectionProvider>(), () =>
        {
            if (!armed)
            { return; }
            protecting.Set();
            Assert.True(commit.Wait(TimeSpan.FromSeconds(10), Token));
        });
        var write = Writer(factory, mqtt, provider, TimeProvider.System);
        var first = await write(null, null, Token);
        var oldMarker = Marker(factory, mqtt);
        armed = true;
        var writing = Task.Run(() => write(first, true, Token), Token);
        Task? running = null;
        using var stop = new CancellationTokenSource();
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim(true);
        var target = new FrozenTarget(() => Marker(factory, mqtt), frozen, release);
        var input = Channel.CreateUnbounded<EdgeMessage>();
        var state = new InputDispatcherState();
        var dispatcher = new InputDispatcherUnit(RuntimeUnitId.Create("input.simulator", "device/input"), input.Reader,
            [target], state, 1, cache, TimeProvider.System, new RecordingEventSink());
        try
        {
            Assert.True(protecting.Wait(TimeSpan.FromSeconds(5), Token));
            Assert.False(cache.HasUndelivered(1));
            var now = DateTimeOffset.UtcNow;
            await input.Writer.WriteAsync(new(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now, []), Token);
            input.Writer.Complete();
            running = dispatcher.RunAsync(stop.Token);
            Assert.NotNull(state.Message); // 已消费输入，但不得在凭证 COMMIT 前冻结身份。
            Assert.Null(target.Frozen);
            commit.Set();
            await writing.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await state.Drained.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var stored = Assert.Single(cache.ReadPending(1, 0, 10));
            Assert.NotEqual(oldMarker, stored.Fingerprint);
            Assert.Equal(Marker(factory, mqtt), stored.Fingerprint);
        }
        finally
        {
            commit.Set();
            await writing;
            await stop.CancelAsync();
            if (running is not null)
            { try { await running; } catch (OperationCanceledException) { } }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WaitingForPublicationIsCancellableAndBoundedWithoutChangingCredentials(bool mqtt, bool timeout)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await ReadyAsync(cache);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var write = Writer(factory, mqtt, factory.Services.GetRequiredService<IDataProtectionProvider>(), clock);
        var first = await write(null, null, Token);
        using var publication = await cache.IdentityBarrier.EnterPublicationAsync([1], Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var writing = write(first, null, stop.Token);
        Assert.False(writing.IsCompleted);
        // 等待时没有 config.db 写事务；另一个真实配置写事务可以立即完成。
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(client => client.Ado.GetInt("SELECT 1"));
        if (timeout)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("credential.publicationBusy", (await Assert.ThrowsAsync<CredentialWriteException>(() => writing)).Code);
        }
        else
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        }
        Assert.Equal(first, mqtt ? factory.Services.GetRequiredService<MqttCredentialStore>().Read(1)!.Version
            : factory.Services.GetRequiredService<HttpCredentialStore>().Read(1)!.Version);
        using var next = await cache.IdentityBarrier.EnterPublicationAsync([1], Token);
        next.Dispose();
        publication.Dispose();
        // 超时/取消没有残留屏障，也没有强制旋转 AccountMarker。
        var marker = Marker(factory, mqtt);
        Assert.NotEqual(first, await write(first, null, Token));
        Assert.Equal(marker, Marker(factory, mqtt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiReturnsExplicitConflictWithoutWritingWhenPublicationCannotDrain(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await ReadyAsync(cache);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var publication = await cache.IdentityBarrier.EnterPublicationAsync([1], Token);
        var path = new Uri($"/api/edge-admin/v1/connections/1/{(mqtt ? "mqtt" : "http")}-credential", UriKind.Relative);
        object request = mqtt
            ? new { recipient = "mqtt://localhost:1883", username = "account", password = "fixture-never-returned" }
            : new { recipient = "http://localhost:8883/", kind = "bearer", secret = "fixture-never-returned" };
        using var response = await client.PutAsJsonAsync(path, request, Token);
        var body = await ProblemAssert.HasStatusAsync(response, HttpStatusCode.Conflict, Token);
        Assert.Contains("credential.publicationBusy", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-never-returned", body, StringComparison.Ordinal);
        Assert.Null(factory.Services.GetRequiredService<HttpCredentialStore>().Read(1));
        Assert.Null(factory.Services.GetRequiredService<MqttCredentialStore>().Read(1));
        Assert.Empty(TestAccounts.Audit(factory.Services, mqtt ? "credential.mqtt.write" : "credential.http.write"));
        publication.Dispose();
        using var retry = await client.PutAsJsonAsync(path, request, Token); // 显式再次提交，不是自动重放。
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(TestAccounts.Audit(factory.Services, mqtt ? "credential.mqtt.write" : "credential.http.write"));
    }

    private static Func<string?, bool?, CancellationToken, Task<string>> Writer(TenonEdgeApplicationFactory factory, bool mqtt,
        IDataProtectionProvider protection, TimeProvider time)
    {
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var audit = factory.Services.GetRequiredService<AuditTrail>();
        var httpStore = new HttpCredentialStore(database, protection, cache, audit, time);
        var mqttStore = new MqttCredentialStore(database, protection, cache, audit, time);
        return async (version, changed, token) => mqtt
            ? (await mqttStore.WriteAsync(1, new("mqtt://localhost:1883", "account", "fixture", ExpectedVersion: version, AccountChanged: changed), "fixture", null, true, token)).Version
            : (await httpStore.WriteAsync(1, new("http://localhost:8883/", HttpCredentialKind.Bearer, Secret: "fixture", ExpectedVersion: version, AccountChanged: changed), "fixture", null, true, token)).Version;
    }

    private static string Marker(TenonEdgeApplicationFactory factory, bool mqtt) => mqtt
        ? factory.Services.GetRequiredService<MqttCredentialStore>().DescribeMqtt(1)!.AccountMarker
        : factory.Services.GetRequiredService<HttpCredentialStore>().DescribeHttp(1)!.AccountMarker;

    private static async Task ReadyAsync(DeliveryCache cache)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (cache.GetStatus().Store is null)
        { await Task.Delay(10, timeout.Token); }
    }

    private sealed class GatedProtection(IDataProtectionProvider inner, Action protect) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new GatedProtector(inner.CreateProtector(purpose), protect);
        private sealed class GatedProtector(IDataProtector inner, Action protect) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new GatedProtector(inner.CreateProtector(purpose), protect);
            public byte[] Protect(byte[] plaintext) { protect(); return inner.Protect(plaintext); }
            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }

    private sealed class FrozenTarget(Func<string> fingerprint, ManualResetEventSlim frozen, ManualResetEventSlim release) : IOutputTarget
    {
        public RuntimeUnitId Id => RuntimeUnitId.Create("output.fixture", "test");
        public long ConnectionId => 1;
        public IReadOnlyList<string> Inputs => ["device/input"];
        public string? ClientId => null;
        public string Recipient => "fixture";
        public string CredentialVersion => "fixture";
        public string CurrentFingerprint => fingerprint();
        public OutputMessage? Frozen { get; private set; }
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
        {
            Frozen = new(Encoding.UTF8.GetBytes("{\"value\":42}"), "events", null, CurrentFingerprint, "fixture");
            frozen.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token));
            return Frozen;
        }
        public IOutputAdapter CreateAdapter() => throw new NotSupportedException();
    }
}
