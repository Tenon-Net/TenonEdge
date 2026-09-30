using TenonEdge.Host.Bootstrap;

namespace TenonEdge.UnitTests.Bootstrap;

/// <summary>
/// 容器数据卷检查（总设计 §10.3，V-08）：从 mountinfo 找出数据目录所在的挂载点，判断是否为持久卷；判断是否在容器中运行。
/// 挂载表样本取自 Docker 容器的实际格式（proc(5)）。
/// </summary>
public sealed class ContainerDataVolumeTests
{
    private const string DockerMountInfo = """
        1500 1400 0:120 / / rw,relatime master:500 - overlay overlay rw,lowerdir=/l,upperdir=/u,workdir=/w
        1501 1500 0:125 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw
        1510 1500 259:2 /var/lib/docker/volumes/tenonedge-data/_data /data rw,relatime master:1 - ext4 /dev/nvme0n1p2 rw
        1511 1500 259:2 /var/lib/docker/containers/abc/hostname /etc/hostname rw,relatime - ext4 /dev/nvme0n1p2 rw
        1512 1500 0:130 / /run/secrets rw,nosuid - tmpfs tmpfs rw
        1513 1500 259:2 /home/user/My\040Data /mnt/my\040data rw,relatime - ext4 /dev/nvme0n1p2 rw
        """;

    [Theory]
    [InlineData("/data", "/data", "ext4", true)]
    [InlineData("/data/tenonedge", "/data", "ext4", true)]
    [InlineData("/var/lib/tenonedge", "/", "overlay", false)]
    [InlineData("/database", "/", "overlay", false)]
    [InlineData("/run/secrets/tenonedge", "/run/secrets", "tmpfs", false)]
    [InlineData("/mnt/my data/tenonedge", "/mnt/my data", "ext4", true)]
    public void DataDirectoryIsMatchedToItsLongestMountPoint(string path, string mountPoint, string fileSystemType, bool persistent)
    {
        var mount = ContainerDataVolume.FindMount(path, DockerMountInfo);

        Assert.NotNull(mount);
        Assert.Equal(mountPoint, mount.MountPoint);
        Assert.Equal(fileSystemType, mount.FileSystemType);
        Assert.Equal(persistent, ContainerDataVolume.IsPersistentVolume(mount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 ext3 /dev/root rw")]
    [InlineData("36 35 98:0")]
    public void MalformedLinesAreIgnored(string line)
    {
        Assert.False(ContainerDataVolume.TryParse(line, out _));
    }

    [Fact]
    public void OptionalFieldsBeforeTheSeparatorAreSkipped()
    {
        Assert.True(ContainerDataVolume.TryParse(
            "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 shared:2 - ext3 /dev/root rw,errors=continue",
            out var entry));

        Assert.Equal(new MountEntry("/mnt2", "ext3"), entry);
    }

    [Theory]
    [InlineData("/.dockerenv", null, null, true)]
    [InlineData("/run/.containerenv", null, null, true)]
    [InlineData(null, "true", null, true)]
    [InlineData(null, null, "podman", true)]
    [InlineData(null, "false", null, false)]
    [InlineData(null, null, null, false)]
    public void ContainerIsRecognizedFromMarkerFilesOrEnvironment(string? existingFile, string? dotnetInContainer, string? container, bool expected)
    {
        var detected = ContainerDataVolume.IsRunningInContainer(
            path => path == existingFile,
            name => name switch
            {
                "DOTNET_RUNNING_IN_CONTAINER" => dotnetInContainer,
                "container" => container,
                _ => null,
            });

        Assert.Equal(expected, detected);
    }
}
