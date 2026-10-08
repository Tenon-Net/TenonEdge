using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Processes;

[Trait("Category", "RealBroker")]
[Trait("Category", "Process")]
public sealed class MqttInputRecoveryProcessTests(MosquittoBrokerFixture fixture, ITestOutputHelper output)
    : IClassFixture<MosquittoBrokerFixture>
{
    private static readonly string[] Inputs = ["device/mqtt"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KilledHostResumesPersistentSubscriptionAndOfflineMessagesWithoutRetainedReplay(bool killBeforeCommit)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "SIGKILL 恢复验证仅在 Linux 执行。");
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        using var work = TemporaryDirectory.Create();
        await using (var bootstrap = HostProcess.Start(work.Path, Environments.Production, data.Path))
        {
            using var api = await ClientAsync(bootstrap);
            using var setup = await api.CompleteSetupAsync(Token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            await api.UseSessionAsync(setup, Token);
            using var credential = await api.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/mqtt-credential", UriKind.Relative),
                new { recipient = $"mqtt://127.0.0.1:{broker.Port}", username = MosquittoBroker.UserName, password = MosquittoBroker.Password }, Token);
            Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
            await StopAsync(bootstrap);
        }
        var topic = MqttProbe.NewTopic("process-input-recovery");
        var seed = Path.Combine(work.Path, "mqtt.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new object[]
            {
                new { kind = "input.mqtt", key = "device/mqtt", configuration = new
                {
                    connectionId = 1, host = "127.0.0.1", port = broker.Port, usePassword = true, topicFilter = topic + "/in",
                    fields = new[] { new { key = "value", path = "/v", type = "integer" } },
                } },
                new { kind = "output.mqtt", key = "out", configuration = new
                { connectionId = 1, host = "127.0.0.1", port = broker.Port, usePassword = true, topic = topic + "/out", inputs = Inputs } },
            },
        }), Token);
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "process-peer-" + Guid.NewGuid().ToString("N")), Token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, topic + "/out", Token);
            await peer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic + "/in").WithPayload("{\"v\":7}")
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
            await using (var first = Start(work.Path, data.Path, seed, broker.Port))
            {
                try
                {
                    using var api = await ClientAsync(first);
                    using var login = await api.LoginAsync("admin", ApiRequests.StrongPassword, Token);
                    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                    await api.UseSessionAsync(login, Token);
                    await OutputPipelineTests.UntilAsync(() => Counter(data.Path, "retainedIgnored") == 1);
                    await PublishAsync(peer, topic, 1);
                    Assert.Equal(1, await NextValueAsync(received));
                    await OutputPipelineTests.UntilAsync(() => Counter(data.Path, "acknowledged") == 2 && Pending(data.Path) == 0);
                    Assert.Equal(1, Counter(data.Path, "subscribed"));
                    Assert.Equal(1, Scalar(data.Path, "config.db", "SELECT COUNT(*) FROM mqtt_subscription WHERE confirmed=1"));
                    if (killBeforeCommit)
                    {
                        using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
                        connection.Open();
                        using var transaction = connection.BeginTransaction(deferred: false);
                        await PublishAsync(peer, topic, 42);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                        deadline.CancelAfter(Timeout);
                        while (true)
                        {
                            var status = await api.GetJsonAsync("/api/edge-admin/v1/runtime/status", deadline.Token);
                            var units = status.GetProperty("runtime").GetProperty("units").EnumerateArray();
                            var reads = units.Select(unit => unit.GetProperty("reads"))
                                .First(element => element.ValueKind != JsonValueKind.Null);
                            if (reads.GetProperty("readsCompleted").GetInt64() >= 2)
                            { break; }
                            await Task.Delay(10, deadline.Token);
                        }
                        Assert.Equal(0, Pending(data.Path));
                        Assert.Equal(2, Counter(data.Path, "acknowledged"));
                        await first.KillAsync(Token); // 持有真实写锁，已解码消息不可能提交；不执行任何产品停机代码。
                        transaction.Rollback();
                    }
                    else
                    { await first.KillAsync(Token); }
                }
                finally { output.WriteLine(first.Output); }
            }
            Assert.Equal(0, Pending(data.Path));
            await PublishAsync(peer, topic, 84); // 确认宿主进程已退出后发布，由仍存活的 Broker 持久会话积存。
            await using var restarted = Start(work.Path, data.Path, seed, broker.Port);
            try
            {
                using var api = await ClientAsync(restarted);
                if (killBeforeCommit)
                { Assert.Equal(42, await NextValueAsync(received)); }
                Assert.Equal(84, await NextValueAsync(received));
                await PublishAsync(peer, topic, 126); // 实时 barrier 排在恢复消息之后，不能被 retained=7 或旧样本=1 取代。
                Assert.Equal(126, await NextValueAsync(received));
                var expectedAcks = killBeforeCommit ? 5 : 4;
                await OutputPipelineTests.UntilAsync(() => Pending(data.Path) == 0 && Counter(data.Path, "acknowledged") == expectedAcks);
                Assert.Equal(1, Counter(data.Path, "subscribed"));
                Assert.Equal(1, Counter(data.Path, "retainedIgnored"));
                Assert.Equal(0, Counter(data.Path, "sessionLost"));
                await StopAsync(restarted);
                Assert.DoesNotContain(MosquittoBroker.Password, restarted.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(ApiRequests.StrongPassword, restarted.Output, StringComparison.Ordinal);
            }
            finally { output.WriteLine(restarted.Output); }
        }
    }

    private static HostProcess Start(string work, string data, string seed, int port) =>
        HostProcess.Start(work, Environments.Production, data,
            [$"--{RuntimeSettings.SeedSnapshotKey}", seed, "--TenonEdge:Outbound:LocalPorts:0", port.ToString(CultureInfo.InvariantCulture)]);

    private static async Task<HttpClient> ClientAsync(HostProcess host)
    {
        var address = await host.WaitForListeningAddressAsync(Timeout, Token);
        var client = new HttpClient { BaseAddress = address, Timeout = Timeout };
        try
        { await host.WaitUntilReadyAsync(client, Timeout, Token); return client; }
        catch
        { client.Dispose(); throw; }
    }

    private static async Task StopAsync(HostProcess host)
    {
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
    }

    private static Task<MqttClientPublishResult> PublishAsync(IMqttClient peer, string topic, int value) =>
        MqttProbe.PublishAsync(peer, topic + "/in", JsonSerializer.SerializeToUtf8Bytes(new { v = value }), MqttQualityOfServiceLevel.AtLeastOnce, Token);

    private static async Task<long> NextValueAsync(ReceivedMessages received)
    {
        using var json = JsonDocument.Parse((await received.NextAsync(Token, Timeout)).ApplicationMessage.ConvertPayloadToString());
        return json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
    }

    private static long Pending(string data) => Scalar(data, "queue.db", "SELECT COUNT(*) FROM delivery");
    private static long Counter(string data, string reason) =>
        Scalar(data, "queue.db", "SELECT COALESCE((SELECT value FROM queue_counters WHERE name=@name),0)", "connection/1/mqtt/" + reason);

    private static long Scalar(string data, string database, string sql, string? name = null)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, database)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (name is not null)
        { command.Parameters.AddWithValue("@name", name); }
        return (long)command.ExecuteScalar()!;
    }
}
