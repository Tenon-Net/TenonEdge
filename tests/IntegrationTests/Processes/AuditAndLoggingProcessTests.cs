using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 审计与日志的真实进程验证（T0-09）：
/// <list type="bullet">
/// <item>日志脱敏（验收“密码、Token、私钥不入日志”；总设计 §3.3、§10.3）：把全部日志类别调到 Trace，走一遍首次设置、错误与正确登录、
/// 修改密码、带会话的读请求和登出，标准输出中不出现任何密码、会话 Cookie、防 CSRF 令牌与密钥环主密钥。
/// 密钥环目录中预先放入一个无法解析的密钥元素，其主密钥为哨兵值：框架在 Trace 级别会把整个元素写入日志（见 SecretProtection）。
/// API 令牌随 T3-09 实现，届时补充同样的检查。</item>
/// <item>审计保留期短于 180 天时拒绝启动（功能需求 §9.2）。</item>
/// </list>
/// </summary>
public sealed class AuditAndLoggingProcessTests(ITestOutputHelper output)
{
    private const string WrongPassword = "Wrong-Password-1";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task SecretsNeverAppearInTheOutputEvenWithTraceLogging()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        var keyRing = Path.Combine(dataDirectory.Path, SecretProtection.KeyRingDirectoryName);
        var sentinelKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "SentinelMasterKey";
        PlantUnreadableKey(keyRing, sentinelKey);

        var secrets = new List<string> { ApiRequests.StrongPassword, ApiRequests.AnotherStrongPassword, WrongPassword, sentinelKey };
        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            additionalArguments:
            [
                "--Logging:LogLevel:Default=Trace",
                "--Logging:LogLevel:Microsoft.AspNetCore=Trace",
                "--Logging:Console:LogLevel:Default=Trace",

                // 带通配符的规则可以比完整类别名更长，同样不能把 XmlKeyManager 调回 Trace。
                $"--Logging:LogLevel:{SecretProtection.KeyManagerLogRule}=Trace",
                $"--Logging:Console:LogLevel:{SecretProtection.KeyManagerLogCategory}*=Trace",
                $"--Logging:Console:LogLevel:{SecretProtection.KeyManagerLogRule}=Trace",
            ]);
        try
        {
            var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies };
            using var client = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);

            void CaptureSessionCookies() => secrets.AddRange(cookies.GetAllCookies().Select(cookie => cookie.Value));

            using (var setup = await client.CompleteSetupAsync(cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
                secrets.Add((await client.UseSessionAsync(setup, cancellationToken)).GetProperty("csrfToken").GetString()!);
                CaptureSessionCookies();
            }

            using (var wrong = await client.LoginAsync("admin", WrongPassword, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            }

            using (var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                secrets.Add((await client.UseSessionAsync(login, cancellationToken)).GetProperty("csrfToken").GetString()!);
                CaptureSessionCookies();
            }

            using (var changed = await client.PostJsonAsync(
                ApiRequests.ChangePassword,
                new { currentPassword = ApiRequests.StrongPassword, newPassword = ApiRequests.AnotherStrongPassword },
                cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
                secrets.Add((await client.UseSessionAsync(changed, cancellationToken)).GetProperty("csrfToken").GetString()!);
                CaptureSessionCookies();
            }

            using (var info = await client.GetAsync(new Uri("/api/edge-admin/v1/system/info", UriKind.Relative), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, info.StatusCode);
            }

            using (var logout = await client.PostAsync(new Uri(ApiRequests.Logout, UriKind.Relative), content: null, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            }

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }

        secrets.AddRange(MasterKeys(keyRing));
        var text = host.Output;

        // 确认确实以 Trace 级别运行，且框架处理了那个无法解析的密钥元素（Error 级别的一条只含去掉子元素的密钥元素）。
        Assert.Contains("trce: ", text, StringComparison.Ordinal);
        Assert.Contains("An exception occurred while processing the key element", text, StringComparison.Ordinal);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task AuditRetentionShorterThan180DaysIsRejectedAtStartup()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            [$"--{DataRetentionSettings.AuditRetentionDaysKey}", "90"]);
        try
        {
            Assert.Equal(HostExitCodes.ConfigurationError, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains("审计保留期不少于 180 天", host.Output, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dataDirectory.Path));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    /// <summary>
    /// 写入一个 id 合法、创建时间无法解析的密钥元素；框架在处理时抛出异常，跳过这个密钥并记录日志。
    /// </summary>
    private static void PlantUnreadableKey(string keyRing, string masterKey)
    {
        PrivateFileSystem.CreateDirectory(keyRing);
        var id = Guid.NewGuid();
        XNamespace dataProtection = "http://schemas.asp.net/2015/03/dataProtection";
        var key = new XElement(
            "key",
            new XAttribute("id", id),
            new XAttribute("version", 1),
            new XElement("creationDate", "not-a-date"),
            new XElement("activationDate", "2026-09-30T00:00:00Z"),
            new XElement("expirationDate", "2026-12-29T00:00:00Z"),
            new XElement(
                "descriptor",
                new XAttribute(
                    "deserializerType",
                    "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection"),
                new XElement(
                    "descriptor",
                    new XElement("encryption", new XAttribute("algorithm", "AES_256_CBC")),
                    new XElement("validation", new XAttribute("algorithm", "HMACSHA256")),
                    new XElement(
                        "masterKey",
                        new XAttribute(XNamespace.Xmlns + "p4", dataProtection),
                        new XAttribute(dataProtection + "requiresEncryption", "true"),
                        new XElement("value", masterKey)))));
        new XDocument(key).Save(Path.Combine(keyRing, $"key-{id:D}.xml"));
    }

    /// <summary>密钥环中全部密钥的主密钥（Linux 上不加密落盘）。</summary>
    private static List<string> MasterKeys(string keyRing) =>
        Directory.EnumerateFiles(keyRing, "key-*.xml")
            .SelectMany(file => XDocument.Load(file).Descendants("value"))
            .Select(value => value.Value)
            .Where(value => value.Length > 0)
            .ToList();

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");
}
