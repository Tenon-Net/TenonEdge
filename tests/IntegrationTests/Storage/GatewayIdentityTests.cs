using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Features.Configuration;
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
    public void DeletedObjectMetadataContributesToTheIdFloorEvenAfterItsRowIsCleared()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var largest = TestIds.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(365), lowBits: 7);
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO configuration_edit(kind,object_id,stamp,deleted,retained) VALUES ('device',@largest,'fixture',1,1)", new { largest });
        Assert.Equal(largest, IdGeneration.ReadFloor(store.Database.Client));
        store.Database.InTransaction(client =>
        {
            ConfigurationEditJournal.Record(client, "device", largest, "deleter", TimeProvider.System, deleted: true);
            ConfigurationEditJournal.PruneCommitted(client, ConfigurationEditJournal.Read(client));
            return true;
        });
        Assert.Equal(0, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM configuration_edit"));
        Assert.Equal(largest, IdGeneration.ReadFloor(store.Database.Client));
        Assert.True(IdGeneration.CreateGenerator(store.Database, TimeProvider.System, NullLogger.Instance).NextId() > largest);
    }

    [Fact]
    public void EveryTableKeyedBySnowflakeIdIsRegisteredForTheFloor()
    {
        // 新增使用雪花主键的表时，必须登记到 IdGeneration.SnowflakeKeyedTables，否则启动时会漏算库中已有的最大 ID。
        using var store = TestConfigStore.Create();
        store.Initialize();
        // 不用雪花主键的表：单行的系统状态、迁移历史，以及以配置版本号为主键的运行快照（版本号的下限由 revision_floor 维护）。
        // mqtt_credential 的主键是种子中的 connectionId（逻辑引用），不分配新的对象 ID；正式连接实体由 T3 管理。
        string[] bookkeepingTables = [SystemStateRecord.TableName, SchemaMigrationRecord.TableName, RuntimeSnapshotRecord.TableName, MqttCredentialRecord.TableName, HttpCredentialRecord.TableName, "output_control", "outbound_policy", "mqtt_subscription", "configuration_draft", "route_draft_reference", "configuration_edit", "delivery_migration", "delivery_migration_intent", "runtime_parameters", "queue_parameters", "trusted_proxy_rules", "configuration_credential_requirement", "connection_trust"];
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('mqtt_credential') WHERE name = 'connection_id' AND pk = 1 AND type = 'INTEGER'"));
        // 待补凭证标记只引用已有连接；新建/替换标记不分配独立编号，连接删除仍推进原 id_floor。
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('configuration_credential_requirement') WHERE name = 'connection_id' AND pk = 1 AND type = 'INTEGER'"));

        // 连接信任同样以已有连接编号寻址；不是独立对象，不新增雪花 ID。
        // 核对真实迁移后的主键，避免把一个新增独立 ID 表误归类为引用表。
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('connection_trust') WHERE name = 'connection_id' AND pk = 1 AND type = 'INTEGER'"));
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('connection_trust') WHERE pk > 0"));

        // 订阅日志按 Broker/ClientId/Filter 三个文本字段联合寻址，不分配雪花 ID。
        Assert.Equal(3, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('mqtt_subscription') WHERE pk > 0 AND type = 'TEXT'"));

        // 路线引用索引用已有路线/来源/连接编号，不分配自己的雪花 ID。
        Assert.Equal(3, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('route_draft_reference') WHERE pk > 0"));

        // 编辑记录以对象种类 + 现有对象编号联合寻址；编号另纳入 ReadFloor，不分配独立 ID。
        Assert.Equal(2, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('configuration_edit') WHERE pk > 0"));
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('configuration_edit') WHERE name = 'object_id' AND pk > 0 AND type = 'INTEGER'"));

        // 迁移尝试以随机操作 UUID 寻址；源/目标是已有对象引用，migration_id 是 queue.db 的代次，不分配雪花主键。
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('delivery_migration') WHERE name = 'operation_id' AND pk = 1 AND type = 'TEXT'"));

        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM pragma_table_info('delivery_migration_intent') WHERE name = 'intent_id' AND pk = 1 AND type = 'TEXT'"));

        // 网页参数/代理规则以固定 id=1 寻址，不分配雪花 ID；迁移须约束为单行。
        foreach (var table in new[] { "runtime_parameters", "queue_parameters", "trusted_proxy_rules" })
        {
            var definition = store.Database.Client.Ado.GetString("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table", new { table });
            Assert.Contains("CHECK(id = 1)", definition, StringComparison.Ordinal);
        }

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
