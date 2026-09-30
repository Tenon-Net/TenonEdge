using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Security;

/// <summary>
/// 持久化密钥环（V-08 进程内部分，AT-19 密钥子集）：密钥在数据目录中；重启、内容根变化后仍能解密；
/// 合并两个密钥环后新旧密文都能解密。每项都有反向对照，证明“能解密”来自持久化的密钥环与固定的应用名。
/// 更换安装路径的真实进程验证见 ListenerProcessTests（HTTPS 证书在重启后保持不变）。
/// </summary>
public sealed class DataProtectionTests
{
    private const string Purpose = "TenonEdge.Tests.DataProtection";

    [Fact]
    public void KeyRingIsPersistedInTheDataDirectoryForOwnerOnly()
    {
        using var directory = TemporaryDirectory.Create();
        using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            _ = factory.Services;
        }

        var keyRing = Path.Combine(directory.Path, SecretProtection.KeyRingDirectoryName);
        Assert.NotEmpty(Directory.EnumerateFiles(keyRing, "key-*.xml"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PrivateFileSystem.DirectoryMode, File.GetUnixFileMode(keyRing));
        }
    }

    [Fact]
    public void CiphertextIsReadableAfterRestartWithADifferentContentRoot()
    {
        using var directory = TemporaryDirectory.Create();
        using var otherContentRoot = TemporaryDirectory.Create();
        string ciphertext;
        using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            ciphertext = Protector(factory.Services).Protect("secret-value");
        }

        using var parent = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var moved = parent.WithWebHostBuilder(builder => builder.UseContentRoot(otherContentRoot.Path));

        Assert.Equal("secret-value", Protector(moved.Services).Unprotect(ciphertext));
    }

    [Fact]
    public void KeyRingIsBoundToTheFixedApplicationName()
    {
        using var directory = TemporaryDirectory.Create();
        string ciphertext;
        using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            ciphertext = Protector(factory.Services).Protect("secret-value");
        }

        var keyRing = new DirectoryInfo(Path.Combine(directory.Path, SecretProtection.KeyRingDirectoryName));

        // 同一目录、同一应用名的独立提供者可以解密：密文只依赖持久化的密钥环与应用名，与宿主实例无关。
        var sameApplication = DataProtectionProvider.Create(keyRing, setup => setup.SetApplicationName(SecretProtection.ApplicationName));
        Assert.Equal("secret-value", sameApplication.CreateProtector(Purpose).Unprotect(ciphertext));

        // 对照：应用名不同即无法解密，说明固定应用名是必要的。
        var otherApplication = DataProtectionProvider.Create(keyRing, setup => setup.SetApplicationName("OtherApplication"));
        Assert.ThrowsAny<CryptographicException>(() => otherApplication.CreateProtector(Purpose).Unprotect(ciphertext));
    }

    [Fact]
    public void MergedKeyRingReadsCiphertextsFromBothSources()
    {
        using var first = TemporaryDirectory.Create();
        using var second = TemporaryDirectory.Create();
        string fromFirst;
        string fromSecond;
        using (var factory = new FixedDataDirectoryApplicationFactory(first.Path))
        {
            fromFirst = Protector(factory.Services).Protect("from-first");
        }

        using (var factory = new FixedDataDirectoryApplicationFactory(second.Path))
        {
            fromSecond = Protector(factory.Services).Protect("from-second");
        }

        // 对照：合并之前，第一个密钥环解不开第二个密钥环保护的数据。
        using (var beforeMerge = new FixedDataDirectoryApplicationFactory(first.Path))
        {
            Assert.ThrowsAny<CryptographicException>(() => Protector(beforeMerge.Services).Unprotect(fromSecond));
        }

        var targetKeyRing = Path.Combine(first.Path, SecretProtection.KeyRingDirectoryName);
        foreach (var key in Directory.EnumerateFiles(Path.Combine(second.Path, SecretProtection.KeyRingDirectoryName), "key-*.xml"))
        {
            File.Copy(key, Path.Combine(targetKeyRing, Path.GetFileName(key)));
        }

        using var merged = new FixedDataDirectoryApplicationFactory(first.Path);
        Assert.Equal("from-first", Protector(merged.Services).Unprotect(fromFirst));
        Assert.Equal("from-second", Protector(merged.Services).Unprotect(fromSecond));
    }

    private static IDataProtector Protector(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
}
