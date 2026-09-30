using System.Text.Json;
using System.Xml.Linq;

namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 读取工程的实际依赖：工程引用与共享框架引用取自还原生成的 obj/project.assets.json（已计入
/// Directory.Build.* 等导入文件与 SDK 隐式加入的引用），包取自 packages.lock.json（含传递依赖）。
/// </summary>
internal static class ProjectFileReader
{
    public const string LockFileName = "packages.lock.json";
    public const string AssetsFileRelativePath = "obj/project.assets.json";

    public static ProjectInfo Read(string projectPath)
    {
        var project = XDocument.Load(projectPath).Root
            ?? throw new InvalidOperationException($"工程文件为空：{projectPath}");
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var assetsFile = Path.Combine(projectDirectory, AssetsFileRelativePath);
        var lockFile = Path.Combine(projectDirectory, LockFileName);
        var (projectReferences, frameworkReferences) = File.Exists(assetsFile)
            ? ReadRestoredReferences(assetsFile)
            : ([], []);

        return new ProjectInfo(
            Name: Path.GetFileNameWithoutExtension(projectPath),
            RelativePath: RepositoryPaths.ToRelative(projectPath),
            Sdk: (string?)project.Attribute("Sdk") ?? string.Empty,
            IsRestored: File.Exists(assetsFile),
            ProjectReferences: projectReferences,
            FrameworkReferences: frameworkReferences,
            LockedPackages: File.Exists(lockFile) ? ReadLockedPackages(lockFile) : null);
    }

    /// <summary>工程文件中带版本特性的 PackageReference（集中版本管理下不应出现）。</summary>
    public static IReadOnlyList<string> FindInlinePackageVersions(string projectPath) =>
        XDocument.Load(projectPath).Descendants("PackageReference")
            .Where(reference => reference.Attribute("Version") is not null
                || reference.Attribute("VersionOverride") is not null
                || reference.Element("Version") is not null
                || reference.Element("VersionOverride") is not null)
            .Select(reference => (string?)reference.Attribute("Include") ?? "(未命名)")
            .ToList();

    private static (List<string> ProjectReferences, List<string> FrameworkReferences) ReadRestoredReferences(string assetsFile)
    {
        using var stream = File.OpenRead(assetsFile);
        using var document = JsonDocument.Parse(stream);
        var project = document.RootElement.GetProperty("project");

        var projectReferences = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var targetFramework in project.GetProperty("restore").GetProperty("frameworks").EnumerateObject())
        {
            if (targetFramework.Value.TryGetProperty("projectReferences", out var references))
            {
                foreach (var reference in references.EnumerateObject())
                {
                    projectReferences.Add(RepositoryPaths.ToRelative(Path.GetFullPath(reference.Name)));
                }
            }
        }

        var frameworkReferences = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetFramework in project.GetProperty("frameworks").EnumerateObject())
        {
            if (targetFramework.Value.TryGetProperty("frameworkReferences", out var references))
            {
                foreach (var reference in references.EnumerateObject())
                {
                    frameworkReferences.Add(reference.Name);
                }
            }
        }

        return ([.. projectReferences], [.. frameworkReferences]);
    }

    private static List<string> ReadLockedPackages(string lockFile)
    {
        using var stream = File.OpenRead(lockFile);
        using var document = JsonDocument.Parse(stream);

        var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetFramework in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var dependency in targetFramework.Value.EnumerateObject())
            {
                // "Project" 条目是工程引用，由还原结果中的工程引用检查。
                if (dependency.Value.GetProperty("type").GetString() != "Project")
                {
                    packages.Add(dependency.Name);
                }
            }
        }

        return [.. packages];
    }
}
