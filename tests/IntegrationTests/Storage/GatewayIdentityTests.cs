using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 网关身份与内部 ID 的起点（T0-06；AT-10 中 NodeId 保留的部分；GW-01“身份重启后不变”）。
/// </summary>
public sealed class GatewayIdentityTests
{
    [Fact]
    public void IdentityIsGeneratedOnceAndKeptWhenTheConfigStoreIsReopened()
    {
        using var directory = TemporaryDirectory.Create();
        GatewayIdentity first;
        using (var store = TestConfigStore.Open(directory.Path))
        {
            store.Initialize();
            first = GatewayIdentity.LoadOrCreate(store.Database, out var created);
            Assert.True(created);
        }

        using (var reopened = TestConfigStore.Open(directory.Path))
        {
            reopened.Initialize();
            var second = GatewayIdentity.LoadOrCreate(reopened.Database, out var created);
            Assert.False(created);
            Assert.Equal(first, second);
        }

        Assert.True(Guid.TryParse(first.NodeId, out _));
        Assert.True(Guid.TryParse(first.ConfigStoreId, out _));
        Assert.NotEqual(first.NodeId, first.ConfigStoreId);
    }

    [Fact]
    public void HostKeepsIdentityAcrossRestartsOnTheSameDataDirectory()
    {
        using var directory = TemporaryDirectory.Create();
        GatewayIdentity first;
        using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            first = factory.Services.GetRequiredService<GatewayIdentity>();
        }

        using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        Assert.Equal(first, restarted.Services.GetRequiredService<GatewayIdentity>());
    }

    [Fact]
    public void SeparateDataDirectoriesGetIndependentIdentities()
    {
        using var first = TestConfigStore.Create();
        using var second = TestConfigStore.Create();
        first.Initialize();
        second.Initialize();

        var a = GatewayIdentity.LoadOrCreate(first.Database, out _);
        var b = GatewayIdentity.LoadOrCreate(second.Database, out _);

        Assert.NotEqual(a.NodeId, b.NodeId);
        Assert.NotEqual(a.ConfigStoreId, b.ConfigStoreId);
    }

    [Fact]
    public void GeneratorStartsAboveTheFloorRecordedInSystemState()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var floorTimestamp = ((DateTimeOffset.UtcNow + TimeSpan.FromDays(365)) - SnowflakeIdGenerator.Epoch).Ticks / TimeSpan.TicksPerMillisecond;
        var floor = (floorTimestamp << 12) | 5;
        store.Database.Client.Ado.ExecuteCommand("UPDATE system_state SET id_floor = @floor WHERE id = 1", new { floor });

        var generator = IdGeneration.CreateGenerator(store.Database, TimeProvider.System, NullLogger.Instance);

        Assert.Equal(floor, IdGeneration.ReadFloor(store.Database.Client));
        Assert.True(generator.NextId() > floor);
    }

    [Fact]
    public void GeneratorStartsAboveTheLargestIdAlreadyInTheDatabase()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var largest = TestIds.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(365), lowBits: 7);
        store.Database.Client.Insertable(new RuntimeEventRecord
        {
            Id = largest,
            FirstOccurredAt = 0,
            LastOccurredAt = 0,
            Level = "info",
            Type = "test.event",
            Message = "测试事件",
        }).ExecuteCommand();

        var generator = IdGeneration.CreateGenerator(store.Database, TimeProvider.System, NullLogger.Instance);

        Assert.Equal(largest, IdGeneration.ReadFloor(store.Database.Client));
        Assert.True(generator.NextId() > largest);
    }

    [Fact]
    public void EveryTableKeyedBySnowflakeIdIsRegisteredForTheFloor()
    {
        // 新增使用雪花主键的表时，必须登记到 IdGeneration.SnowflakeKeyedTables，否则启动时会漏算库中已有的最大 ID。
        using var store = TestConfigStore.Create();
        store.Initialize();
        // 不用雪花主键的表：单行的系统状态、迁移历史，以及以配置版本号为主键的运行快照（版本号的下限由 revision_floor 维护）。
        // mqtt_credential 的主键是种子中的 connectionId（逻辑引用），不分配新的对象 ID；正式连接实体由 T3 管理。
        string[] bookkeepingTables = [SystemStateRecord.TableName, SchemaMigrationRecord.TableName, RuntimeSnapshotRecord.TableName, MqttCredentialRecord.TableName, "output_control", "outbound_policy"];
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('mqtt_credential') WHERE name = 'connection_id' AND pk = 1 AND type = 'INTEGER'"));

        var tables = store.Database.Client.Ado
            .SqlQuery<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .Except(bookkeepingTables)
            .ToList();

        Assert.Equal(IdGeneration.SnowflakeKeyedTables.Order(StringComparer.Ordinal), tables);
        foreach (var table in tables)
        {
            Assert.Equal(1, store.Database.Client.Ado.GetInt(
                "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = 'id' AND pk = 1 AND type = 'INTEGER'",
                new { table }));
        }
    }
}
