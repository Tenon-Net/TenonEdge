namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 用构造的工程描述验证依赖规则本身：合规的结构不报错，典型违规都能被发现。
/// </summary>
public sealed class ProjectDependencyRulesTests
{
    private const string HostPath = "src/TenonEdge.Host/TenonEdge.Host.csproj";
    private const string RuntimePath = "src/TenonEdge.Runtime/TenonEdge.Runtime.csproj";
    private const string AdaptersPath = "src/TenonEdge.Adapters/TenonEdge.Adapters.csproj";
    private const string AbstractionsPath = "src/TenonEdge.Abstractions/TenonEdge.Abstractions.csproj";

    [Fact]
    public void DesignedFourProjectStructureHasNoViolations()
    {
        ProjectInfo[] projects =
        [
            Project("TenonEdge.Host", "Microsoft.NET.Sdk.Web",
                references: [RuntimePath, AdaptersPath, AbstractionsPath],
                packages: ["Microsoft.AspNetCore.OpenApi", "SqlSugarCoreNoDrive", "Microsoft.Data.Sqlite", "MQTTnet"]),
            Project("TenonEdge.Runtime", references: [AbstractionsPath],
                packages: ["Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "Microsoft.Extensions.Hosting.Abstractions"]),
            Project("TenonEdge.Adapters", references: [AbstractionsPath],
                packages: ["MQTTnet", "NModbus"]),
            Project("TenonEdge.Abstractions"),
        ];

        Assert.Empty(ProjectDependencyRules.Check(projects));
    }

    [Theory]
    [InlineData("TenonEdge.Runtime")]
    [InlineData("TenonEdge.Adapters")]
    [InlineData("TenonEdge.Abstractions")]
    public void ReferencingHostIsReported(string projectName)
    {
        var violations = ProjectDependencyRules.Check([Project(projectName, references: [HostPath])]);

        Assert.Contains(violations, violation => violation.Contains("不允许引用 TenonEdge.Host", StringComparison.Ordinal));
    }

    [Fact]
    public void AdaptersReferencingRuntimeIsReported()
    {
        var violations = ProjectDependencyRules.Check([Project("TenonEdge.Adapters", references: [RuntimePath])]);

        Assert.Contains(violations, violation => violation.Contains("不允许引用 TenonEdge.Runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void AbstractionsDependingOnOrmIsReported()
    {
        var violations = ProjectDependencyRules.Check(
            [Project("TenonEdge.Abstractions", packages: ["SqlSugarCoreNoDrive"])]);

        Assert.Contains(violations, violation => violation.Contains("SqlSugarCoreNoDrive", StringComparison.Ordinal));
    }

    [Fact]
    public void AbstractionsDependingOnAnyPackageIsReported()
    {
        var violations = ProjectDependencyRules.Check(
            [Project("TenonEdge.Abstractions", packages: ["Newtonsoft.Json"])]);

        Assert.Contains(violations, violation => violation.Contains("只能依赖 .NET 基础类型", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeDependingOnProtocolLibraryTransitivelyIsReported()
    {
        // 锁文件包含传递依赖：即使协议库经由其他包引入，也会出现在依赖闭包中。
        var violations = ProjectDependencyRules.Check(
            [Project("TenonEdge.Runtime", packages: ["Some.Wrapper", "MQTTnet"])]);

        Assert.Contains(violations, violation => violation.Contains("禁止的包 MQTTnet", StringComparison.Ordinal));
    }

    [Fact]
    public void AdaptersDependingOnSqliteIsReported()
    {
        var violations = ProjectDependencyRules.Check(
            [Project("TenonEdge.Adapters", packages: ["Microsoft.Data.Sqlite"])]);

        Assert.Contains(violations, violation => violation.Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal));
    }

    [Fact]
    public void AspNetCoreOutsideHostIsReported()
    {
        ProjectInfo[] projects =
        [
            Project("TenonEdge.Runtime", frameworks: ["Microsoft.AspNetCore.App"]),
            Project("TenonEdge.Adapters", "Microsoft.NET.Sdk.Web"),
            Project("TenonEdge.Abstractions", packages: ["Microsoft.AspNetCore.Http.Abstractions"]),
        ];

        var violations = ProjectDependencyRules.Check(projects);

        Assert.Contains(violations, violation => violation.StartsWith("TenonEdge.Runtime：不得引用 Microsoft.AspNetCore.App", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.StartsWith("TenonEdge.Adapters：应使用 Microsoft.NET.Sdk", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.StartsWith("TenonEdge.Abstractions：只能依赖 .NET 基础类型", StringComparison.Ordinal));
    }

    [Fact]
    public void UnregisteredSourceProjectIsReported()
    {
        var violations = ProjectDependencyRules.Check([Project("TenonEdge.Common")]);

        Assert.Contains(violations, violation => violation.Contains("未登记的工程", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceProjectReferencingTestProjectIsReported()
    {
        var violations = ProjectDependencyRules.Check(
        [
            Project("TenonEdge.Host", "Microsoft.NET.Sdk.Web",
                references: ["tests/UnitTests/TenonEdge.UnitTests.csproj"]),
        ]);

        Assert.Contains(violations, violation => violation.Contains("不得引用 src 以外的工程", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectWithoutLockFileIsReported()
    {
        var project = Project("TenonEdge.Runtime") with { LockedPackages = null };

        var violations = ProjectDependencyRules.Check([project]);

        Assert.Contains(violations, violation => violation.Contains(ProjectFileReader.LockFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void UnrestoredProjectIsReported()
    {
        var project = Project("TenonEdge.Runtime") with { IsRestored = false };

        var violations = ProjectDependencyRules.Check([project]);

        Assert.Contains(violations, violation => violation.Contains("尚未还原", StringComparison.Ordinal));
    }

    private static ProjectInfo Project(
        string name,
        string sdk = "Microsoft.NET.Sdk",
        string[]? references = null,
        string[]? frameworks = null,
        string[]? packages = null) =>
        new(
            Name: name,
            RelativePath: $"src/{name}/{name}.csproj",
            Sdk: sdk,
            IsRestored: true,
            ProjectReferences: references ?? [],
            FrameworkReferences: frameworks ?? [],
            LockedPackages: packages ?? []);
}
