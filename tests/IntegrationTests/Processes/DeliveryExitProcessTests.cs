using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Processes;

public sealed class DeliveryExitProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("beforeAck")]
    [InlineData("ackBeforeDelete")]
    [InlineData("afterDelete")]
    [InlineData("beforeCommit")]
    [Trait("Category", "Process")]
    public async Task ForcedExitPreservesUnDeletedDeliveryAndReplaysTheSameFrozenPayload(string boundary)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程退出验证仅在 Linux 执行。");
        using var data = TemporaryDirectory.Create();
        using var work = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seed = Path.Combine(work.Path, "seed.json");
        var period = boundary == "beforeCommit" ? 10000 : 60000;
        await File.WriteAllTextAsync(seed, $$$$"""
            {"schemaVersion":1,"units":[
              {"kind":"input.simulator","key":"meter/sim","configuration":{"periodMs":{{{{period}}}},"sample":{"value":42}}},
              {"kind":"output.mqtt","key":"broker","configuration":{"connectionId":1,"host":"127.0.0.1","port":{{{{port}}}},"topic":"exit-test","qos":1,"timeoutMs":60000,"inputs":["meter/sim"]}}
            ]}
            """, Token);
        byte[] frozen;
        await using (var host = Start(work.Path, data.Path, seed, port))
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(Timeout, Token);
                var publish = await ConnectAndReadPublishAsync(peer);
                frozen = publish.Payload;
                using var json = JsonDocument.Parse(frozen);
                Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt32());
                Assert.Equal(1L, Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery WHERE state='pending'"));
                if (boundary == "ackBeforeDelete")
                {
                    Sql(data.Path, "CREATE TRIGGER hold_delete BEFORE DELETE ON delivery BEGIN SELECT abs(-9223372036854775808); END;");
                }
                if (boundary != "beforeAck")
                {
                    await AckAsync(peer, publish.PacketId);
                    if (boundary == "ackBeforeDelete")
                    {
                        // 删除请求只能在生产适配器已返回 Ack 后产生。触发器令删除回滚，确定卡在确认/删除之间。
                        await OutputPipelineTests.UntilAsync(() => Scalar(data.Path, "config.db",
                            "SELECT COUNT(*) FROM runtime_event WHERE type='storage.fault'") > 0);
                        Assert.Equal(1L, Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery"));
                    }
                    else
                    {
                        await OutputPipelineTests.UntilAsync(() => Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery") == 0);
                    }
                }
                if (boundary == "beforeCommit")
                {
                    using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
                    connection.Open();
                    using var transaction = connection.BeginTransaction(deferred: false);
                    var address = await host.WaitForListeningAddressAsync(Timeout, Token);
                    using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                    using var client = new HttpClient(handler) { BaseAddress = address, Timeout = Timeout };
                    using var setup = await client.CompleteSetupAsync(Token);
                    Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
                    await client.UseSessionAsync(setup, Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    deadline.CancelAfter(Timeout);
                    while (true)
                    {
                        var status = await client.GetJsonAsync("/api/edge-admin/v1/runtime/status", deadline.Token);
                        var read = status.GetProperty("runtime").GetProperty("units").EnumerateArray()
                            .Select(unit => unit.GetProperty("reads")).First(element => element.ValueKind != JsonValueKind.Null);
                        if (read.GetProperty("readsCompleted").GetInt64() >= 2)
                        {
                            Assert.True(status.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64() > 0);
                            break;
                        }
                        await Task.Delay(25, deadline.Token);
                    }
                    Assert.Equal(0L, Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery"));
                    Assert.False(peer.GetStream().DataAvailable);
                    await host.KillAsync(Token); // 仍持有写锁：第二条已读取，但不可能提交或发送。
                    transaction.Rollback();
                }
                else
                { await host.KillAsync(Token); }
            }
            finally { output.WriteLine(host.Output); }
        }
        Assert.Equal(boundary is "afterDelete" or "beforeCommit" ? 0L : 1L, Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery"));
        if (boundary == "ackBeforeDelete")
        { Sql(data.Path, "DROP TRIGGER hold_delete"); }
        await using var restarted = Start(work.Path, data.Path, seed, port);
        try
        {
            using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(Timeout, Token);
            var replay = await ConnectAndReadPublishAsync(peer);
            if (boundary is "afterDelete" or "beforeCommit")
            {
                using var old = JsonDocument.Parse(frozen);
                using var current = JsonDocument.Parse(replay.Payload);
                Assert.NotEqual(old.RootElement.GetProperty("messageId").GetString(), current.RootElement.GetProperty("messageId").GetString());
            }
            else
            { Assert.Equal(frozen, replay.Payload); }
            await AckAsync(peer, replay.PacketId);
            if (boundary is not ("afterDelete" or "beforeCommit"))
            {
                // 重启也立即产生一条新采样；旧 FIFO 队头先重放，然后新采样。
                var next = await ReadPublishAsync(peer.GetStream());
                Assert.NotEqual(Convert.ToBase64String(frozen), Convert.ToBase64String(next.Payload));
                await AckAsync(peer, next.PacketId);
            }
            await OutputPipelineTests.UntilAsync(() => Scalar(data.Path, "queue.db", "SELECT COUNT(*) FROM delivery") == 0);
            await restarted.RequestStopAsync(Token);
            Assert.Equal(0, await restarted.WaitForExitAsync(Timeout, Token));
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static HostProcess Start(string work, string data, string seed, int port) => HostProcess.Start(work, Environments.Production, data,
        [$"--{RuntimeSettings.SeedSnapshotKey}", seed, "--TenonEdge:Outbound:LocalPorts:0", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static async Task<(byte[] Payload, byte[] PacketId)> ConnectAndReadPublishAsync(TcpClient peer)
    {
        var stream = peer.GetStream();
        var connect = await ReadPacketAsync(stream);
        Assert.Equal(0x10, connect.Header);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, Token); // MQTT 3.1.1 CONNACK
        return await ReadPublishAsync(stream);
    }

    private static async Task<(byte[] Payload, byte[] PacketId)> ReadPublishAsync(NetworkStream stream)
    {
        var packet = await ReadPacketAsync(stream);
        Assert.Equal(0x32, packet.Header); // QoS 1，非 retained PUBLISH
        var topicLength = (packet.Body[0] << 8) | packet.Body[1];
        var offset = 2 + topicLength;
        return (packet.Body[(offset + 2)..], packet.Body[offset..(offset + 2)]);
    }

    private static async Task<(int Header, byte[] Body)> ReadPacketAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Timeout);
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, deadline.Token);
        var header = one[0];
        var length = 0;
        var factor = 1;
        for (var i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(one, deadline.Token);
            length += (one[0] & 127) * factor;
            if ((one[0] & 128) == 0)
            { break; }
            factor *= 128;
        }
        Assert.InRange(length, 0, 300000);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, deadline.Token);
        return (header, body);
    }

    private static ValueTask AckAsync(TcpClient peer, byte[] packetId) =>
        peer.GetStream().WriteAsync(new byte[] { 0x40, 2, packetId[0], packetId[1] }, Token);

    private static long Scalar(string data, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, database)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Sql(string data, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static TimeSpan Timeout => TimeSpan.FromSeconds(30);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
