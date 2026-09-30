namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 测试隔离辅助本身的行为：临时目录互不重叠，用后连同内容删除。
/// </summary>
public sealed class TestInfrastructureTests
{
    [Fact]
    public void TemporaryDirectoriesAreDistinctAndRemovedWithTheirContent()
    {
        string firstPath;
        string secondPath;
        using (var first = TemporaryDirectory.Create())
        using (var second = TemporaryDirectory.Create())
        {
            File.WriteAllText(Path.Combine(first.Path, "probe.txt"), "probe");
            Directory.CreateDirectory(Path.Combine(second.Path, "nested"));

            Assert.NotEqual(first.Path, second.Path);
            Assert.StartsWith(Path.GetTempPath(), first.Path, StringComparison.Ordinal);
            firstPath = first.Path;
            secondPath = second.Path;
        }

        Assert.False(Directory.Exists(firstPath));
        Assert.False(Directory.Exists(secondPath));
    }
}
