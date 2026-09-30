namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 测试独占的临时目录，位于系统临时目录下，释放时连同内容删除。
/// 测试只在这类目录中读写运行数据，不访问正式数据目录。
/// </summary>
public sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tenonedge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
