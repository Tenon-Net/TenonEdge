using System.Reflection;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 程序版本：宿主程序集的 InformationalVersion（由 SDK 在构建时生成，含源码提交标识）。
/// 迁移历史、运行事件与系统信息使用同一个值；正式版本号的规则随发布验证（T0-12、T0-13）确定。
/// </summary>
internal static class ProductVersion
{
    public static string Current { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
