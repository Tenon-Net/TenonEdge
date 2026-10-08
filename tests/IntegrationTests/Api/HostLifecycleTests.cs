using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Api;

/// <summary>
/// 宿主生命周期（T0-10；BE-AT-03 基础，V-01 中“后台服务未处理异常的默认行为”；总设计 §8.1）：
/// <list type="bullet">
/// <item>.NET 默认在后台服务抛出未处理异常时停止整个宿主，所以运行单元的异常必须在单元边界捕获；产品宿主保留这一默认值作为最后防线。</item>
/// <item>现有后台服务（保留期清理）在自己的边界捕获异常，失败时宿主照常服务。</item>
/// <item>后台服务是单例，每个只创建一次。目前没有按具体类型登记的后台服务，“具体类型登记的单例与后台服务是同一个实例”这一检查用于防回归，
/// 并用构造的错误登记证明它有效（T1 的运行时宿主按正确写法登记）。</item>
/// <item>没有登记 IHttpContextAccessor，单例与后台服务取不到 HttpContext；单例依赖作用域服务由作用域校验拒绝（见 ServiceProviderValidationTests）。</item>
/// </list>
/// </summary>
public sealed class HostLifecycleTests(ITestOutputHelper output)
{
    private const string BlockedPurge = "audit purge blocked by test";
    private const string HostLogCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AnUnhandledBackgroundServiceExceptionStopsTheHostByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var capture = new LogCapture();
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(capture);
        builder.Services.AddHostedService<FailingService>();
        using var host = builder.Build();

        Assert.Equal(
            BackgroundServiceExceptionBehavior.StopHost,
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);

        await host.StartAsync(cancellationToken);

        // 宿主随之停止，但等待停机的调用正常返回，不抛出后台服务的异常。Program 在 RunAsync 返回后结束，
        // 因此按目前的写法进程会以退出码 0 结束；T1-01 的运行时宿主须据下面的 ExecuteTask 状态改为非零退出码。
        await host.WaitForShutdownAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        foreach (var entry in capture.Entries.Where(entry => entry.Level >= LogLevel.Error))
        {
            output.WriteLine($"{entry.Level} {entry.Category}: {entry.Message}");
        }

        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.IsCancellationRequested);
        Assert.Contains(capture.Entries, entry => entry is { Level: LogLevel.Error, Category: HostLogCategory, Message: "BackgroundService failed" });
        Assert.Contains(capture.Entries, entry => entry.Level == LogLevel.Critical
            && entry.Category == HostLogCategory
            && entry.Message.Contains("BackgroundServiceExceptionBehavior is configured to StopHost", StringComparison.Ordinal));
        var failed = host.Services.GetServices<IHostedService>().OfType<FailingService>().Single();
        Assert.True(failed.ExecuteTask is { IsFaulted: true });
    }

    [Fact]
    public void TheProductHostKeepsStopHostAsTheLastResort()
    {
        // 运行单元的异常应在单元边界捕获（T1 监督者）。万一漏出，宿主停止，而不是让后台循环悄悄停在失败状态；
        // 停止后的退出码按上一个测试的观察由 T1-01 处理。
        using var factory = new TenonEdgeApplicationFactory();

        Assert.Equal(
            BackgroundServiceExceptionBehavior.StopHost,
            factory.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);
    }

    [Fact]
    public async Task ARetentionFailureIsCaughtInsideTheServiceAndTheHostKeepsServing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var dataDirectory = TemporaryDirectory.Create();
        await using (var first = new FixedDataDirectoryApplicationFactory(dataDirectory.Path))
        {
            // 一条早已过期的审计记录，加上拒绝删除审计记录的触发器：下次启动后的第一次清理失败，启动检查本身不受影响。
            var occurredAt = DateTimeOffset.UtcNow.AddDays(-400);
            var database = first.Services.GetRequiredService<ConfigDatabase>();
            database.Client.Insertable(new AuditLogRecord
            {
                Id = TestIds.At(occurredAt, lowBits: 1),
                OccurredAt = occurredAt.ToUnixTimeMilliseconds(),
                Action = "test.expired",
                Result = AuditResults.Success,
            }).ExecuteCommand();
            database.Client.Ado.ExecuteCommand(
                $"CREATE TRIGGER block_audit_purge BEFORE DELETE ON {AuditLogRecord.TableName} BEGIN SELECT RAISE(ABORT, '{BlockedPurge}'); END");
        }

        var capture = new LogCapture();
        await using var baseFactory = new FixedDataDirectoryApplicationFactory(dataDirectory.Path,
            configureServices: services => services.AddSingleton<IClockSyncStatus>(new TestClockSyncStatus()));
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        using var client = factory.CreateClient();

        var failure = await WaitForAsync(
            () => capture.Matching(typeof(DataRetentionService).FullName!, "按保留期清理审计记录与运行事件失败") is [var entry, ..] ? entry : null,
            cancellationToken);

        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Contains(BlockedPurge, failure.Message, StringComparison.Ordinal);
        using var liveness = await client.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public void HostedServicesAreSingletonsCreatedOnce()
    {
        using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;

        var hosted = services.GetServices<IHostedService>().ToList();
        output.WriteLine(string.Join(Environment.NewLine, hosted.Select(service => service.GetType().FullName)));

        Assert.Contains(hosted, service => service is DataRetentionService);
        Assert.Empty(FindDuplicateHostedServiceInstances(services));

        // 运行时（T1-01，BE-AT-03）：协调者是唯一的运行时后台服务，与按具体类型登记的单例是同一个实例；
        // 它驱动的运行时宿主也只有一个。
        var coordinator = Assert.Single(hosted.OfType<RuntimeCoordinator>());
        Assert.Same(services.GetRequiredService<RuntimeCoordinator>(), coordinator);
        Assert.Same(services.GetRequiredService<RuntimeHost>(), coordinator.Runtime);
        using var scope = services.CreateScope();
        Assert.Same(coordinator.Runtime, scope.ServiceProvider.GetRequiredService<RuntimeHost>());
    }

    [Fact]
    public void TheHostedServiceCheckCatchesASecondInstanceRegisteredByConcreteType()
    {
        // 运行时协调者既按具体类型登记、又作为后台服务运行（T1-01），上一个用例由此覆盖了这一分支；
        // 这里再用构造的登记证明检查本身有效。错误写法：AddSingleton 与 AddHostedService 各自创建一个实例，会有两套循环。
        var wrong = new ServiceCollection();
        wrong.AddSingleton<ProbeService>();
        wrong.AddHostedService<ProbeService>();
        using var wrongProvider = wrong.BuildServiceProvider();

        Assert.Contains(FindDuplicateHostedServiceInstances(wrongProvider), violation => violation.Contains(nameof(ProbeService), StringComparison.Ordinal));

        // 正确写法（运行时协调者按此登记）：后台服务取自同一个单例。
        var right = new ServiceCollection();
        right.AddSingleton<ProbeService>();
        right.AddHostedService(provider => provider.GetRequiredService<ProbeService>());
        using var rightProvider = right.BuildServiceProvider();

        Assert.Empty(FindDuplicateHostedServiceInstances(rightProvider));
    }

    [Fact]
    public void TheRequestContextIsNotAvailableToSingletons()
    {
        using var factory = new TenonEdgeApplicationFactory();

        Assert.Null(factory.Services.GetService<IHttpContextAccessor>());
    }

    /// <summary>后台服务应为单例、每个类型只有一个实例，按具体类型登记的单例与作为后台服务运行的必须是同一个实例。返回违规说明。</summary>
    private static List<string> FindDuplicateHostedServiceInstances(IServiceProvider services)
    {
        var violations = new List<string>();
        var first = services.GetServices<IHostedService>().ToList();
        var second = services.GetServices<IHostedService>().ToList();
        if (first.Count != second.Count || first.Zip(second).Any(pair => !ReferenceEquals(pair.First, pair.Second)))
        {
            violations.Add("两次解析得到的后台服务不是同一组实例。");
        }

        foreach (var group in first.GroupBy(service => service.GetType()).Where(group => group.Count() > 1))
        {
            violations.Add($"{group.Key.Name} 作为后台服务有 {group.Count()} 个实例。");
        }

        foreach (var service in first)
        {
            var registered = services.GetService(service.GetType());
            if (registered is not null && !ReferenceEquals(registered, service))
            {
                violations.Add($"{service.GetType().Name} 按具体类型登记的实例与作为后台服务运行的不是同一个。");
            }
        }

        return violations;
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        while (true)
        {
            if (probe() is { } result)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private sealed class ProbeService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class FailingService : BackgroundService
    {
        public const string Failure = "模拟的运行单元异常";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException(Failure);
        }
    }
}
