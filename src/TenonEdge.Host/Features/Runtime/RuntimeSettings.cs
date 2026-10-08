using System.Globalization;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行时的部署参数（T1-01）：
/// <list type="bullet">
/// <item><c>TenonEdge:Runtime:SeedSnapshot</c>：种子快照文件的绝对路径（可选）。T1 尚无配置界面，运行计划由种子文件提供（总设计 §12 T1）；
/// 内容变化后的下一次启动导入为新的有效版本，内容不变时不重复导入。格式见 <see cref="RuntimePlanDocument"/>。</item>
/// <item><c>TenonEdge:Runtime:StableAfterSeconds</c>：激活后连续运行多久记为“已稳定”，默认 60 秒（总设计 §8.4），范围 1—86400。</item>
/// </list>
/// 路径不是绝对路径、数值无效属于部署参数错误，宿主不启动；种子文件缺失或内容无效只记运行事件，不影响管理界面。
/// </summary>
internal sealed record RuntimeSettings(string? SeedSnapshotPath, TimeSpan StableAfter)
{
    public const string SeedSnapshotKey = "TenonEdge:Runtime:SeedSnapshot";

    public const string StableAfterSecondsKey = "TenonEdge:Runtime:StableAfterSeconds";

    public const int DefaultStableAfterSeconds = 60;

    public const int MaximumStableAfterSeconds = 86_400;

    public static RuntimeSettings Resolve(IConfiguration configuration)
    {
        var seed = configuration[SeedSnapshotKey];
        if (string.IsNullOrWhiteSpace(seed))
        {
            seed = null;
        }
        else if (!Path.IsPathFullyQualified(seed))
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"部署参数 {SeedSnapshotKey} 必须是绝对路径，当前为“{seed}”。");
        }

        var stableAfter = TimeSpan.FromSeconds(DefaultStableAfterSeconds);
        var value = configuration[StableAfterSecondsKey];
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds is < 1 or > MaximumStableAfterSeconds)
            {
                throw new HostStartupException(
                    HostExitCodes.ConfigurationError,
                    $"部署参数 {StableAfterSecondsKey} 的值“{value}”无效：应为 1 到 {MaximumStableAfterSeconds} 之间的整数（秒）。");
            }

            stableAfter = TimeSpan.FromSeconds(seconds);
        }

        return new RuntimeSettings(seed, stableAfter);
    }
}
