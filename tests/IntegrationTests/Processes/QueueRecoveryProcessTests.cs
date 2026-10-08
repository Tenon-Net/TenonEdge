using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

public sealed class QueueRecoveryProcessTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Process")]
    public async Task CorruptedQueueIsArchivedWhileAccountAndGatewayIdentitySurviveRestart()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试仅在 Linux 执行。");
        using var working = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        var token = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(30);
        string? nodeId = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var host = HostProcess.Start(working.Path, Environments.Production, data.Path);
            try
            {
                var address = await host.WaitForListeningAddressAsync(timeout, token);
                using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
                using var client = new HttpClient(handler) { BaseAddress = address, Timeout = timeout };
                await host.WaitUntilReadyAsync(client, timeout, token);
                using var login = attempt == 0
                    ? await client.CompleteSetupAsync(token)
                    : await client.LoginAsync("admin", ApiRequests.StrongPassword, token);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                await client.UseSessionAsync(login, token);
                var info = await client.GetJsonAsync("/api/edge-admin/v1/system/info", token);
                if (attempt == 0)
                { nodeId = info.GetProperty("nodeId").GetString(); }
                else
                { Assert.Equal(nodeId, info.GetProperty("nodeId").GetString()); }
                var status = await client.GetJsonAsync("/api/edge-admin/v1/runtime/status", token);
                Assert.False(status.GetProperty("queue").GetProperty("storageFault").GetBoolean());
                await host.RequestStopAsync(token);
                Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(timeout, token));
            }
            finally { output.WriteLine(host.Output); }
            if (attempt == 0)
            {
                await File.WriteAllTextAsync(Path.Combine(data.Path, "queue.db"), "corrupt-queue-sentinel", token);
            }
        }
        var archive = Assert.Single(Directory.GetDirectories(data.Path, "queue.db.corrupt-*"));
        Assert.Equal("corrupt-queue-sentinel", await File.ReadAllTextAsync(Path.Combine(archive, "queue.db"), token));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "config.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runtime_event WHERE type='storage.queueRebuilt' AND level='error'";
        Assert.Equal(1L, command.ExecuteScalar());
    }
}
