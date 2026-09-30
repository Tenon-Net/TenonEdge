namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 后端工程依赖方向规则（后端实施规范 §2，BE-AT-01）：
/// <code>
/// Host         -> Runtime / Adapters / Abstractions / 配置存储所需组件
/// Runtime      -> Abstractions / Microsoft.Data.Sqlite / 必要的 .NET 基础设施
/// Adapters     -> Abstractions / 必要协议库
/// Abstractions -> .NET 基础类型
/// </code>
/// 规则覆盖四个工程职责；尚未创建的工程在创建后自动受同一规则约束。
/// </summary>
internal static class ProjectDependencyRules
{
    public const string SourceDirectoryPrefix = "src/";

    private const string WebSdk = "Microsoft.NET.Sdk.Web";
    private const string DefaultSdk = "Microsoft.NET.Sdk";
    private const string AspNetCoreFramework = "Microsoft.AspNetCore.App";

    private static readonly string[] AspNetCorePackages = ["Microsoft.AspNetCore."];
    private static readonly string[] OrmPackages = ["SqlSugar", "Microsoft.EntityFrameworkCore"];
    private static readonly string[] SqlitePackages = ["Microsoft.Data.Sqlite", "SQLitePCLRaw."];
    private static readonly string[] ProtocolPackages = ["MQTTnet", "NModbus", "FluentModbus"];

    private static readonly Dictionary<string, ProjectRule> Rules = new(StringComparer.Ordinal)
    {
        ["TenonEdge.Host"] = new(
            Sdk: WebSdk,
            AllowedProjectReferences: ["TenonEdge.Runtime", "TenonEdge.Adapters", "TenonEdge.Abstractions"],
            AllowsAspNetCoreFramework: true,
            AllowsPackages: true,
            ForbiddenPackagePrefixes: []),
        ["TenonEdge.Runtime"] = new(
            Sdk: DefaultSdk,
            AllowedProjectReferences: ["TenonEdge.Abstractions"],
            AllowsAspNetCoreFramework: false,
            AllowsPackages: true,
            ForbiddenPackagePrefixes: [.. AspNetCorePackages, .. OrmPackages, .. ProtocolPackages]),
        ["TenonEdge.Adapters"] = new(
            Sdk: DefaultSdk,
            AllowedProjectReferences: ["TenonEdge.Abstractions"],
            AllowsAspNetCoreFramework: false,
            AllowsPackages: true,
            ForbiddenPackagePrefixes: [.. AspNetCorePackages, .. OrmPackages, .. SqlitePackages]),
        ["TenonEdge.Abstractions"] = new(
            Sdk: DefaultSdk,
            AllowedProjectReferences: [],
            AllowsAspNetCoreFramework: false,
            AllowsPackages: false,
            ForbiddenPackagePrefixes: []),
    };

    /// <summary>检查 src 下的工程，返回全部违规说明；没有违规时返回空列表。</summary>
    public static IReadOnlyList<string> Check(IEnumerable<ProjectInfo> sourceProjects)
    {
        var violations = new List<string>();
        foreach (var project in sourceProjects)
        {
            if (!Rules.TryGetValue(project.Name, out var rule))
            {
                violations.Add(
                    $"{project.RelativePath}：未登记的工程。后端只保留 Host、Runtime、Adapters、Abstractions 四个工程，" +
                    "新增工程须先修改依赖规则并说明理由。");
                continue;
            }

            CheckSdk(project, rule, violations);
            if (project.IsRestored)
            {
                CheckProjectReferences(project, rule, violations);
                CheckFrameworkReferences(project, rule, violations);
            }
            else
            {
                violations.Add(
                    $"{project.Name}：尚未还原（缺少 {ProjectFileReader.AssetsFileRelativePath}），无法核对实际生效的工程与框架引用。");
            }

            CheckPackages(project, rule, violations);
        }

        return violations;
    }

    private static void CheckSdk(ProjectInfo project, ProjectRule rule, List<string> violations)
    {
        if (!string.Equals(project.Sdk, rule.Sdk, StringComparison.Ordinal))
        {
            violations.Add($"{project.Name}：应使用 {rule.Sdk}，实际为 {Display(project.Sdk)}。");
        }
    }

    private static void CheckProjectReferences(ProjectInfo project, ProjectRule rule, List<string> violations)
    {
        foreach (var reference in project.ProjectReferences)
        {
            if (!reference.StartsWith(SourceDirectoryPrefix, StringComparison.Ordinal))
            {
                violations.Add($"{project.Name}：不得引用 src 以外的工程 {reference}。");
                continue;
            }

            var referencedName = Path.GetFileNameWithoutExtension(reference);
            if (!rule.AllowedProjectReferences.Contains(referencedName, StringComparer.Ordinal))
            {
                violations.Add($"{project.Name}：不允许引用 {referencedName}（{reference}）。");
            }
        }
    }

    private static void CheckFrameworkReferences(ProjectInfo project, ProjectRule rule, List<string> violations)
    {
        if (!rule.AllowsAspNetCoreFramework
            && project.FrameworkReferences.Contains(AspNetCoreFramework, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add($"{project.Name}：不得引用 {AspNetCoreFramework} 共享框架。");
        }
    }

    private static void CheckPackages(ProjectInfo project, ProjectRule rule, List<string> violations)
    {
        if (project.LockedPackages is null)
        {
            violations.Add($"{project.Name}：缺少 {ProjectFileReader.LockFileName}，无法核对实际还原的依赖。");
            return;
        }

        if (!rule.AllowsPackages && project.LockedPackages.Count > 0)
        {
            violations.Add($"{project.Name}：只能依赖 .NET 基础类型，实际依赖 {string.Join(", ", project.LockedPackages)}。");
            return;
        }

        var forbidden = project.LockedPackages
            .Where(package => rule.ForbiddenPackagePrefixes.Any(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (forbidden.Count > 0)
        {
            violations.Add($"{project.Name}：依赖闭包中出现禁止的包 {string.Join(", ", forbidden)}。");
        }
    }

    private static string Display(string value) => value.Length == 0 ? "(未声明)" : value;

    private sealed record ProjectRule(
        string Sdk,
        string[] AllowedProjectReferences,
        bool AllowsAspNetCoreFramework,
        bool AllowsPackages,
        string[] ForbiddenPackagePrefixes);
}
