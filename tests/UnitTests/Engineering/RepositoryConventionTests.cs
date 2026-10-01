using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 对实际仓库执行的工程规则：依赖方向、SDK 固定、集中版本、锁文件与唯一解决方案
/// （后端实施规范 §2、§5，BE-AT-01、BE-AT-04）。
/// </summary>
public sealed partial class RepositoryConventionTests
{
    [Fact]
    public void SourceProjectsFollowDependencyDirection()
    {
        var sourceProjects = RepositoryPaths.FindProjectFiles()
            .Select(ProjectFileReader.Read)
            .Where(project => project.RelativePath.StartsWith(ProjectDependencyRules.SourceDirectoryPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Contains(sourceProjects, project => project.Name == "TenonEdge.Host");
        var violations = ProjectDependencyRules.Check(sourceProjects);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ReferencesAreReadFromRestoreResultsIncludingImplicitOnes()
    {
        // Host 工程文件没有 FrameworkReference 项：Web SDK 隐式加入的 Microsoft.AspNetCore.App 只能从还原结果读到，
        // 经 Directory.Build.* 导入的引用同理。
        var host = ProjectFileReader.Read(Path.Combine(RepositoryPaths.SourceDirectory, "TenonEdge.Host", "TenonEdge.Host.csproj"));
        Assert.True(host.IsRestored);
        Assert.Contains("Microsoft.AspNetCore.App", host.FrameworkReferences);

        var unitTests = ProjectFileReader.Read(Path.Combine(RepositoryPaths.TestsDirectory, "UnitTests", "TenonEdge.UnitTests.csproj"));
        Assert.Equal(
            ["src/TenonEdge.Abstractions/TenonEdge.Abstractions.csproj", "src/TenonEdge.Host/TenonEdge.Host.csproj"],
            unitTests.ProjectReferences);
    }

    [Fact]
    public void GlobalJsonPinsExactSdkWithoutRollForward()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "global.json")));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Matches(ExactVersion(), sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Fact]
    public void PackageVersionsAreManagedCentrally()
    {
        var packagesProps = XDocument.Load(Path.Combine(RepositoryPaths.Root, "Directory.Packages.props"));
        Assert.Equal("true", packagesProps.Descendants("ManagePackageVersionsCentrally").Single().Value.Trim());

        var inlineVersions = RepositoryPaths.FindProjectFiles()
            .SelectMany(path => ProjectFileReader.FindInlinePackageVersions(path)
                .Select(package => $"{RepositoryPaths.ToRelative(path)}：{package}"))
            .ToList();
        Assert.True(inlineVersions.Count == 0, "工程文件中不应声明包版本：" + string.Join("；", inlineVersions));
    }

    [Fact]
    public void EveryProjectHasLockFile()
    {
        var projects = RepositoryPaths.FindProjectFiles();
        Assert.NotEmpty(projects);

        var missing = projects
            .Where(path => !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, ProjectFileReader.LockFileName)))
            .Select(RepositoryPaths.ToRelative)
            .ToList();
        Assert.True(missing.Count == 0, "缺少 packages.lock.json：" + string.Join("；", missing));
    }

    [Fact]
    public void SingleSolutionListsEveryProject()
    {
        var solutionFiles = Directory.EnumerateFiles(RepositoryPaths.Root, "*.sln*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal([RepositoryPaths.SolutionFileName], solutionFiles);

        var listed = File.ReadLines(Path.Combine(RepositoryPaths.Root, RepositoryPaths.SolutionFileName))
            .Select(line => SolutionProjectLine().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["path"].Value.Replace('\\', '/'))
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        var onDisk = RepositoryPaths.FindProjectFiles().Select(RepositoryPaths.ToRelative).ToList();

        Assert.Equal(onDisk, listed);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactVersion();

    [GeneratedRegex("""^Project\("\{[0-9A-Fa-f-]+\}"\)\s*=\s*"[^"]*",\s*"(?<path>[^"]+)""")]
    private static partial Regex SolutionProjectLine();
}
