namespace TenonEdge.Runtime.Diagnostics;

/// <summary>运行时产生的运行事件类型代码（功能需求 §7.4）。发布后不得修改已有的值：查询按它筛选。</summary>
public static class RuntimeEventTypes
{
    /// <summary>运行单元异常退出，按退避重启。</summary>
    public const string UnitFaulted = "runtime.unitFaulted";

    /// <summary>运行单元停止时出错（例如取消回调中释放连接失败）；停止照常进行。</summary>
    public const string UnitStopFailed = "runtime.unitStopFailed";

    /// <summary>运行单元在停止期限内没有退出，仍占用其标识，需重启服务（总设计 §7.4 单实例规则）。</summary>
    public const string UnitStopTimedOut = "runtime.unitStopTimedOut";

    /// <summary>运行单元构建失败（结构性问题，例如缺少模块、配置无效），该单元不启动。</summary>
    public const string UnitBuildFailed = "runtime.unitBuildFailed";

    /// <summary>设备连续 3 个周期整体读取失败，判为离线。</summary>
    public const string DeviceOffline = "device.offline";

    /// <summary>离线设备恢复读取。</summary>
    public const string DeviceOnline = "device.online";

    /// <summary>发送缓存写入失败（存储异常），本批已显式回滚。</summary>
    public const string StorageFault = "storage.fault";

    /// <summary>发送缓存写入恢复正常。</summary>
    public const string StorageRecovered = "storage.recovered";
}
