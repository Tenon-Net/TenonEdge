namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// 同一测试类共用一个真实 Broker。本机不能运行 Docker 或没有镜像时不启动，测试经 <see cref="Require"/> 写明原因后跳过。
/// </summary>
public sealed class MosquittoBrokerFixture : IAsyncLifetime
{
    internal MosquittoBroker? Broker { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (MosquittoBroker.UnavailableReason is null)
        {
            Broker = await MosquittoBroker.StartAsync(TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Broker is not null)
        {
            await Broker.DisposeAsync();
        }
    }

    internal MosquittoBroker Require()
    {
        Assert.SkipWhen(Broker is null, MosquittoBroker.UnavailableReason ?? "测试 Broker 没有启动。");
        return Broker!;
    }

    /// <summary>需要独占 Broker 的用例（暂停容器、冻结连接）单独启动一个。</summary>
    internal static async Task<MosquittoBroker> StartDedicatedAsync(CancellationToken cancellationToken)
    {
        Assert.SkipWhen(MosquittoBroker.UnavailableReason is not null, MosquittoBroker.UnavailableReason ?? string.Empty);
        return await MosquittoBroker.StartAsync(cancellationToken);
    }
}
