using System.Security.Cryptography;
using TenonEdge.Abstractions.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>
/// 系统信息（system 分组，总设计 §10.1；功能需求 §10.1）：只读状态查询，所有角色可查看。
/// 返回目前已有数据的项目：版本、NodeId、网关名称、数据目录及其所在文件系统的空间、HTTPS 证书指纹与到期时间、
/// 实际加载的 SQLite 版本与编译选项（V-07）、首次设置的时间与来源 IP（总设计 §10.3）、阻止运行时启动的原因（总设计 §2.3）。
/// 时钟同步状态来自只读内核检测，无法判断时为 unknown。
/// </summary>
internal static class SystemInfoEndpoints
{
    public static RouteGroupBuilder MapSystemInfoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/system/info", Get)
            .WithTags("System")
            .WithName("getSystemInfo")
            .WithSummary("查看系统信息")
            .WithDescription(
                "所有角色可查看：版本、NodeId、网关名称、数据目录与磁盘空间、HTTPS 证书的 SHA-256 指纹与到期时间、" +
                "实际加载的 SQLite 版本、首次设置的时间与来源 IP，以及阻止运行时启动的原因。");
        return api;
    }

    private static Ok<SystemInfoResponse> Get(
        GatewayIdentity identity,
        DataDirectoryLock dataDirectory,
        HttpsCertificateStore certificates,
        ConfigStoreInitializer configStore,
        ConfigDatabase database,
        RuntimeStartConditions runtimeStartConditions,
        IClockSyncStatus clock)
    {
        var state = database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        var certificate = certificates.Certificate;
        var sqlite = configStore.SqliteRuntime;
        var root = dataDirectory.Directory.Root;

        return TypedResults.Ok(new SystemInfoResponse(
            ProductVersion.Current,
            identity.NodeId,
            state.GatewayName,
            root,
            DiskSpaceOf(root),
            new HttpsCertificateInfo(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero)),
            new ConfigStoreInfo(configStore.SchemaVersion, sqlite.Version, sqlite.CompileOptions),
            state.SetupCompletedAt is { } completedAt
                ? new FirstSetupInfo(DateTimeOffset.FromUnixTimeMilliseconds(completedAt), state.SetupSourceIp)
                : null,
            runtimeStartConditions.Blockers,
            clock.State));
    }

    /// <summary>数据目录所在文件系统的空间；无法读取时为空，不让整个系统信息失败。</summary>
    private static DiskSpaceInfo? DiskSpaceOf(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return new DiskSpaceInfo(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>系统信息。firstSetup 为空表示尚未完成首次设置；runtimeStartBlockers 为空表示运行时可以启动。</summary>
internal sealed record SystemInfoResponse(
    string Version,
    string NodeId,
    string? GatewayName,
    string DataDirectory,
    DiskSpaceInfo? DataDirectoryDisk,
    HttpsCertificateInfo HttpsCertificate,
    ConfigStoreInfo ConfigStore,
    FirstSetupInfo? FirstSetup,
    IReadOnlyList<RuntimeStartBlocker> RuntimeStartBlockers,
    ClockSyncState ClockSyncState);

/// <summary>文件系统空间（字节）；可用空间为非特权进程可用的部分。</summary>
internal sealed record DiskSpaceInfo(long TotalBytes, long AvailableBytes);

/// <summary>当前 HTTPS 证书：SHA-256 指纹（十六进制大写，无分隔符）与到期时间。脚本据此信任自签证书（功能需求 §12.3）。</summary>
internal sealed record HttpsCertificateInfo(string Sha256Fingerprint, DateTimeOffset NotAfter);

/// <summary>配置库：schema 版本，以及实际加载的 SQLite 原生库版本与编译选项。</summary>
internal sealed record ConfigStoreInfo(int SchemaVersion, string SqliteVersion, IReadOnlyList<string> SqliteCompileOptions);

/// <summary>最近一次完成首次设置（含重置后重新设置）的时间与来源 IP，供安装人员核对是否为自己的地址（总设计 §10.3）。</summary>
internal sealed record FirstSetupInfo(DateTimeOffset CompletedAt, string? SourceIp);
