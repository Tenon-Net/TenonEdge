using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Storage;

/// <summary>
/// SQLite 原生库版本下限（总设计 §2.3，S-18）：3.7.0 至 3.51.2 存在 WAL 重置缺陷，3.51.3 起修复；
/// 低于下限或版本号无法识别时都不允许启动运行时。
/// </summary>
public sealed class SqliteRuntimeInfoTests
{
    [Theory]
    [InlineData("3.51.3", true)]
    [InlineData("3.53.3", true)]
    [InlineData("3.100.0", true)]
    [InlineData("4.0.0", true)]
    [InlineData("3.51.2", false)]
    [InlineData("3.49.1", false)]
    [InlineData("3.7.0", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    public void VersionMustBeAtLeastTheWalResetFix(string version, bool supported)
    {
        Assert.Equal(supported, SqliteRuntimeInfo.IsSupportedVersion(version));
    }

    [Fact]
    public void MinimumVersionIsTheWalResetFixRelease()
    {
        Assert.Equal(new Version(3, 51, 3), SqliteRuntimeInfo.MinimumVersion);
    }
}
