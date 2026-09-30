namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 定位仓库根目录：从测试程序集所在目录向上查找解决方案文件。
/// </summary>
internal static class RepositoryPaths
{
    public const string SolutionFileName = "TenonEdge.sln";

    public static string Root { get; } = FindRoot();

    public static string SourceDirectory => Path.Combine(Root, "src");

    public static string TestsDirectory => Path.Combine(Root, "tests");

    /// <summary>src 与 tests 下的全部工程文件，按仓库相对路径排序。</summary>
    public static IReadOnlyList<string> FindProjectFiles() =>
        new[] { SourceDirectory, TestsDirectory }
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => ToRelative(path), StringComparer.Ordinal)
            .ToList();

    /// <summary>转为使用 '/' 分隔的仓库相对路径。</summary>
    public static string ToRelative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        var segments = ToRelative(path).Split('/');
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上找到 {SolutionFileName}，无法定位仓库根目录。");
    }
}
