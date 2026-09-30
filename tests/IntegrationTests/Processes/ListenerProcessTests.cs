using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// HTTP 与 HTTPS 监听的真实进程验证（T0-06；V-08、AT-19 密钥子集中“更换安装路径后仍可解密”）：
/// 真实 TLS 握手出示的是数据目录中保存的自签证书；更换安装路径并重启后证书不变（证书私钥经密钥环保护，解不开就会重新生成）；
/// HTTP 可以关闭；监听参数错误以退出码 2 结束。
/// </summary>
public sealed partial class ListenerProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task HostServesHttpAndHttpsWithTheStoredSelfSignedCertificateAndNoHsts()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            var httpAddress = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
            var httpsAddress = await host.WaitForHttpsAddressAsync(StartupTimeout, cancellationToken);
            using var http = new HttpClient { BaseAddress = httpAddress, Timeout = TimeSpan.FromSeconds(10) };
            await host.WaitUntilReadyAsync(http, StartupTimeout, cancellationToken);

            using (var response = await http.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var (status, presented, hsts) = await GetOverHttpsAsync(httpsAddress, "/healthz", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(hsts, "HTTPS 响应不应带 Strict-Transport-Security（总设计 §10.3：首版不发送 HSTS）。");
            Assert.Equal(presented.SubjectName.Name, presented.IssuerName.Name);
            Assert.Equal(CreatedFingerprint(host.Output), presented.GetCertHashString(HashAlgorithmName.SHA256));

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task HttpsCertificateIsDecryptedAfterRestartFromAnotherInstallPath()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        using var otherInstallPath = TemporaryDirectory.Create();

        string originalFingerprint;
        await using (var original = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            var (_, presented) = await StartAndReadCertificateAsync(original, cancellationToken);
            originalFingerprint = presented.GetCertHashString(HashAlgorithmName.SHA256);
            Assert.Equal(CreatedFingerprint(original.Output), originalFingerprint);
            await original.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await original.WaitForExitAsync(ExitTimeout, cancellationToken));
            output.WriteLine(original.Output);
        }

        CopyDirectory(AppContext.BaseDirectory, otherInstallPath.Path);
        await using var moved = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            programDirectory: otherInstallPath.Path);
        try
        {
            var (_, presented) = await StartAndReadCertificateAsync(moved, cancellationToken);

            Assert.Equal(originalFingerprint, presented.GetCertHashString(HashAlgorithmName.SHA256));
            Assert.Contains($"Content root path: {otherInstallPath.Path}", moved.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("已生成 HTTPS 自签证书", moved.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("无法解密", moved.Output, StringComparison.Ordinal);

            await moved.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await moved.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(moved.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task HttpListenerCanBeDisabledByDeploymentParameter()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            ["--TenonEdge:Http:Enabled", "false"]);
        try
        {
            await host.WaitForApplicationStartedAsync(StartupTimeout, cancellationToken);
            var httpsAddress = await host.WaitForHttpsAddressAsync(StartupTimeout, cancellationToken);

            Assert.DoesNotContain("Now listening on: http://", host.Output, StringComparison.Ordinal);
            var (status, _, _) = await GetOverHttpsAsync(httpsAddress, "/healthz", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, status);

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task InvalidListenerSettingIsReportedWithConfigurationExitCode()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            ["--TenonEdge:Https:Port", "70000"]);
        try
        {
            Assert.Equal(HostExitCodes.ConfigurationError, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains("不是有效的端口", host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening on", host.Output, StringComparison.Ordinal);

            // 监听参数在取得数据目录锁之前校验，参数错误时不创建任何数据。
            Assert.Empty(Directory.EnumerateFileSystemEntries(dataDirectory.Path));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    private static async Task<(Uri HttpsAddress, X509Certificate2 Presented)> StartAndReadCertificateAsync(
        HostProcess host,
        CancellationToken cancellationToken)
    {
        var httpAddress = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
        var httpsAddress = await host.WaitForHttpsAddressAsync(StartupTimeout, cancellationToken);
        using var http = new HttpClient { BaseAddress = httpAddress, Timeout = TimeSpan.FromSeconds(10) };
        await host.WaitUntilReadyAsync(http, StartupTimeout, cancellationToken);
        var (status, presented, _) = await GetOverHttpsAsync(httpsAddress, "/readyz", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, status);
        return (httpsAddress, presented);
    }

    /// <summary>经 HTTPS 请求，返回状态码、服务器实际出示的证书以及是否带 HSTS 头。</summary>
    private static async Task<(HttpStatusCode Status, X509Certificate2 Presented, bool Hsts)> GetOverHttpsAsync(
        Uri httpsAddress,
        string path,
        CancellationToken cancellationToken)
    {
        X509Certificate2? presented = null;
        using var handler = new HttpClientHandler
        {
            // 自签证书不在信任链中：只接受“链不受信任”这一种错误，并记下服务器实际出示的证书。
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    presented = X509CertificateLoader.LoadCertificate(certificate.RawData);
                }

                return errors == SslPolicyErrors.RemoteCertificateChainErrors;
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = httpsAddress, Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return (response.StatusCode, presented ?? throw new InvalidOperationException("没有取得服务器证书。"), response.Headers.Contains("Strict-Transport-Security"));
    }

    private static string CreatedFingerprint(string hostOutput)
    {
        var match = CreatedCertificateLine().Match(hostOutput);
        Assert.True(match.Success, "启动日志中没有生成证书的记录。");
        return match.Groups["fingerprint"].Value;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    [GeneratedRegex("已生成 HTTPS 自签证书，SHA-256 指纹 (?<fingerprint>[0-9A-F]{64})")]
    private static partial Regex CreatedCertificateLine();
}
