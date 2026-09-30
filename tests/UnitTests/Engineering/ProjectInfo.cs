namespace TenonEdge.UnitTests.Engineering;

/// <summary>
/// 依赖检查使用的工程描述。引用取自还原结果，包含经 Directory.Build.* 等导入文件加入的引用。
/// </summary>
/// <param name="Name">工程名（工程文件名去掉扩展名）。</param>
/// <param name="RelativePath">工程文件的仓库相对路径。</param>
/// <param name="Sdk">工程文件声明的 MSBuild SDK。</param>
/// <param name="IsRestored">是否已还原（存在 obj/project.assets.json）；未还原时无法核对实际生效的引用。</param>
/// <param name="ProjectReferences">实际生效的直接工程引用（仓库相对路径）。</param>
/// <param name="FrameworkReferences">实际生效的共享框架引用，包括 SDK 隐式加入的引用。</param>
/// <param name="LockedPackages">
/// packages.lock.json 中实际还原的全部包（直接与传递依赖）；没有锁文件时为 null。
/// </param>
internal sealed record ProjectInfo(
    string Name,
    string RelativePath,
    string Sdk,
    bool IsRestored,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyList<string>? LockedPackages);
