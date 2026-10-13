using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 发送缓存 queue.db 的文件、表结构与连接（总设计 §8.2、§8.5，后端实施规范 §4）：
/// <list type="bullet">
/// <item>由 Runtime 直接通过 Microsoft.Data.Sqlite 读写，与 config.db 分开；新建文件只有属主可以读写，-wal、-shm 沿用其权限。</item>
/// <item>journal_mode=WAL（持久保存在库文件中）。写者的连接与初始化连接显式设置 synchronous=FULL 与 busy_timeout 并回读，
/// 不依赖原生库的编译默认值（V-07）；读连接只执行查询，不设置 synchronous（它只影响写入的持久性）。
/// 实际加载的 SQLite 低于 3.51.3 时拒绝打开（WAL 重置缺陷，总设计 §2.3）。</item>
/// <item>写连接只供唯一的写者使用；读取使用短连接与短事务，WAL 下只能读到已提交的记录，也不阻塞写者。</item>
/// <item>表结构版本保存在 <c>PRAGMA user_version</c> 中，按版本顺序迁移；由更新版本的程序迁移过的库拒绝打开（不支持降级）。</item>
/// </list>
/// </summary>
internal sealed partial class DeliveryStore
{
    public const int SchemaVersion = 9;

    public const string ExpectedJournalMode = "wal";

    /// <summary>PRAGMA synchronous 回读的是数值，FULL 为 2。</summary>
    public const long SynchronousFull = 2;

    public const int BusyTimeoutMilliseconds = 5000;

    private const string ApplySettingsSql = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";

    private const string SelectColumns = """
        seq, delivery_id, connection_id, source_message_id, device_key, route_id, node_label, revision, payload,
        target_address, qos, headers, fingerprint, fingerprint_summary, created_at, failure_count, state,
        quarantine_reason, last_error, migration_id, failed_at, byte_size, clock_anomaly, protocol_metadata, last_retried_at
        """;

    /// <summary>各版本的迁移语句；只追加，不修改已发布的版本。</summary>
    private static readonly string[] Migrations =
    [
        // 版本 1（T1-03）：库的归属与投递记录（总设计 §8.2、§10.2）。seq 用 AUTOINCREMENT，删除之后也不复用，
        // 发送者按 seq 游标顺序读取时不会漏掉新记录；同一目标内按 seq 先进先出。
        """
        CREATE TABLE queue_meta (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            config_store_id TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE TABLE delivery (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            delivery_id TEXT NOT NULL UNIQUE,
            connection_id INTEGER NOT NULL,
            source_message_id TEXT NOT NULL,
            device_key TEXT NOT NULL,
            route_id INTEGER,
            node_label TEXT,
            revision INTEGER NOT NULL,
            payload BLOB NOT NULL,
            target_address TEXT,
            qos INTEGER,
            headers TEXT,
            fingerprint TEXT NOT NULL,
            fingerprint_summary TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            failure_count INTEGER NOT NULL DEFAULT 0,
            state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'failed', 'quarantined')),
            quarantine_reason TEXT,
            last_error TEXT,
            migration_id INTEGER
        );
        CREATE INDEX ix_delivery_connection_state_seq ON delivery (connection_id, state, seq);
        """,
        // 版本 2（T1-05）：失败区时间与按目标策略计数，全部由队列写者维护。
        """
        ALTER TABLE delivery ADD COLUMN failed_at INTEGER;
        CREATE INDEX ix_delivery_failed_at ON delivery (failed_at) WHERE state = 'failed';
        CREATE TABLE delivery_failure_totals (
            connection_id INTEGER PRIMARY KEY,
            rejected INTEGER NOT NULL DEFAULT 0,
            poisoned INTEGER NOT NULL DEFAULT 0,
            overflowed INTEGER NOT NULL DEFAULT 0,
            expired INTEGER NOT NULL DEFAULT 0
        );
        """,
        // 版本 3（T1-06）：隔离、配额与保留期核心。逻辑字节与 DeliveryDraft.ByteSize 一致。
        """
        ALTER TABLE delivery ADD COLUMN byte_size INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE delivery ADD COLUMN clock_anomaly INTEGER NOT NULL DEFAULT 0;
        UPDATE delivery SET byte_size = length(payload) + 256 + length(CAST(device_key AS BLOB))
            + length(CAST(COALESCE(node_label, '') AS BLOB)) + length(CAST(COALESCE(target_address, '') AS BLOB))
            + length(CAST(fingerprint AS BLOB)) + length(CAST(fingerprint_summary AS BLOB))
            + length(CAST(COALESCE(headers, '') AS BLOB));
        CREATE TABLE delivery_queue_totals (
            connection_id INTEGER PRIMARY KEY,
            quota_rejected INTEGER NOT NULL DEFAULT 0,
            expired INTEGER NOT NULL DEFAULT 0,
            quarantined INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_delivery_state_created ON delivery (state, created_at);
        """,
        // 版本 4（T1-07）：全局策略/运行计数，由唯一写者维护。
        """
        CREATE TABLE queue_counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL DEFAULT 0);
        """,
        // 版本 5（T2-06）：非秘密协议约定随报文冻结；旧记录保留 null，既有配额字节不变。
        """
        ALTER TABLE delivery ADD COLUMN protocol_metadata TEXT;
        """,
        // 版本 6（T3-08）：人工重试单独刷新待发保留期，不覆盖首次创建时间或原报文。
        """
        ALTER TABLE delivery ADD COLUMN last_retried_at INTEGER;
        """,
        // 版本 7（T3-08）：业务迁移完成记录。操作 UUID 来自持久意图；队列编号不随 config.db 恢复而复用。
        // 与所选记录同事务提交，跨库状态/审计恢复时以此为准；不能在未同步 config.db 时删除。
        """
        CREATE TABLE delivery_migration_receipt (
            migration_id INTEGER PRIMARY KEY AUTOINCREMENT,
            config_store_id TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            request_hash TEXT NOT NULL,
            source_connection_id INTEGER NOT NULL,
            target_connection_id INTEGER NOT NULL,
            affected_records INTEGER NOT NULL CHECK (affected_records >= 0),
            completed_at INTEGER NOT NULL,
            UNIQUE (config_store_id, operation_id)
        );
        """,
        // 版本 8（T3-08）：形式 A 等待隔离的持久归属与原状态；隔离收据防止重启时扩大同一意图的范围。
        """
        ALTER TABLE delivery ADD COLUMN migration_intent_id TEXT;
        ALTER TABLE delivery ADD COLUMN migration_previous_state TEXT CHECK (migration_previous_state IN ('pending', 'failed', 'quarantined'));
        CREATE INDEX ix_delivery_migration_intent ON delivery (migration_intent_id) WHERE migration_intent_id IS NOT NULL;
        CREATE TABLE delivery_migration_hold (
            config_store_id TEXT NOT NULL,
            intent_id TEXT NOT NULL,
            request_hash TEXT NOT NULL,
            connection_id INTEGER NOT NULL,
            old_fingerprint TEXT NOT NULL,
            held_records INTEGER NOT NULL CHECK (held_records >= 0),
            created_at INTEGER NOT NULL,
            PRIMARY KEY (config_store_id, intent_id)
        );
        """,
        // 版本 9（T3-08）：受正文权限保护的HTTP响应摘要；不混入last_error/只读元数据。
        """
        ALTER TABLE delivery ADD COLUMN response_summary TEXT
            CHECK (response_summary IS NULL OR length(CAST(response_summary AS BLOB)) <= 8192);
        """,
    ];

    private readonly DeliveryCacheOptions _options;
    private readonly string _writeConnectionString;
    private readonly string _readConnectionString;
    private readonly TimeProvider _time;
    private readonly Lock _initializeLock = new();
    private DeliveryStoreInfo? _info;

    public DeliveryStore(DeliveryCacheOptions options, TimeProvider time, TenonEdge.Abstractions.Contracts.IRuntimeEventSink? events = null)
    {
        _options = options.Validate();
        _events = events;
        _time = time;
        _maintenanceStart = time.GetUtcNow();
        _maintenanceTimestamp = time.GetTimestamp();
        _writeConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        _readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string FilePath => _options.DatabasePath;

    public DeliveryStoreInfo? Info
    {
        get
        {
            lock (_initializeLock)
            {
                return _info;
            }
        }
    }

    /// <summary>打开并迁移 queue.db（只执行一次）。失败时抛出 <see cref="SqliteException"/>、<see cref="IOException"/> 或 <see cref="DeliveryStoreException"/>。</summary>
    public DeliveryStoreInfo EnsureInitialized()
    {
        lock (_initializeLock)
        {
            if (_info is not null)
            {
                return _info;
            }

            FinishInterruptedRecovery();
            try
            {
                return Initialize();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
            {
                // 仅初始化时恢复确定的损坏；运行中的读写故障不能在活跃连接旁替换数据库。
                BeginRecovery();
                FinishInterruptedRecovery();
                return Initialize();
            }
        }
    }

    private DeliveryStoreInfo Initialize()
    {
        CreateFileForOwnerOnly(_options.DatabasePath);
        using var connection = Open(_writeConnectionString);
        var version = Convert.ToString(Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture) ?? string.Empty;
        if (!Version.TryParse(version, out var parsed) || parsed < new Version(3, 51, 3))
        {
            throw new DeliveryStoreException($"实际加载的 SQLite 原生库版本为 {version}，低于要求的 3.51.3，不打开发送缓存。");
        }

        var journalMode = Convert.ToString(Scalar(connection, "PRAGMA journal_mode = WAL;"), CultureInfo.InvariantCulture);
        if (!string.Equals(journalMode, ExpectedJournalMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryStoreException($"发送缓存无法启用 WAL 日志模式，当前为 {journalMode}。");
        }

        var check = Convert.ToString(Scalar(connection, "PRAGMA quick_check(1);"), CultureInfo.InvariantCulture);
        if (!string.Equals(check, "ok", StringComparison.Ordinal))
        {
            throw new SqliteException("发送缓存完整性检查失败。", 11);
        }

        var settings = ApplySettings(connection);
        var schemaVersion = Migrate(connection);
        var configStoreId = EnsureMeta(connection);
        _info = new DeliveryStoreInfo(_options.DatabasePath, version, settings.JournalMode, settings.Synchronous, schemaVersion, configStoreId);
        return _info;
    }

    /// <summary>打开写者使用的连接：显式设置并回读持久化参数，不符时抛出 <see cref="DeliveryStoreException"/>。</summary>
    public SqliteConnection OpenWriteConnection()
    {
        EnsureInitialized();
        var connection = Open(_writeConnectionString);
        try
        {
            ApplySettings(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 按 seq 顺序读取某个目标已提交的待发记录（短连接、短事务）。<paramref name="afterSeq"/> 是调用方已经读到的最大 seq，第一次读取传 0。
    /// </summary>
    public IReadOnlyList<StoredDelivery> ReadPending(long connectionId, long afterSeq, int limit)
        => Read(connectionId, afterSeq, limit, DeliveryState.Pending);

    internal IReadOnlyList<StoredDelivery> Read(long connectionId, long afterSeq, int limit, DeliveryState state, int? belowFailureCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM delivery
            WHERE connection_id = $connectionId AND state = $state AND seq > $afterSeq
                AND ($below IS NULL OR failure_count < $below)
            ORDER BY seq
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$connectionId", connectionId);
        command.Parameters.AddWithValue("$afterSeq", afterSeq);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$state", state == DeliveryState.Failed ? "failed" : state == DeliveryState.Quarantined ? "quarantined" : "pending");
        command.Parameters.AddWithValue("$below", (object?)belowFailureCount ?? DBNull.Value);
        var records = new List<StoredDelivery>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                records.Add(ReadRecord(reader));
            }
        }

        transaction.Commit();
        return records;
    }

    /// <summary>关闭连接池中的读连接（停机时调用）；之后的读取会重新打开连接。</summary>
    public void ClearReadPool()
    {
        using var connection = new SqliteConnection(_readConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>插入一条投递记录，返回分配的 seq。只由写者在其事务中调用。</summary>
    public static long Insert(SqliteCommand command, DeliveryDraft draft, long createdAt)
    {
        command.Parameters["$deliveryId"].Value = draft.DeliveryId.ToString("D");
        command.Parameters["$connectionId"].Value = draft.ConnectionId;
        command.Parameters["$sourceMessageId"].Value = draft.SourceMessageId.ToString("D");
        command.Parameters["$deviceKey"].Value = draft.DeviceKey;
        command.Parameters["$routeId"].Value = (object?)draft.RouteId ?? DBNull.Value;
        command.Parameters["$nodeLabel"].Value = (object?)draft.NodeLabel ?? DBNull.Value;
        command.Parameters["$revision"].Value = draft.Revision;
        command.Parameters["$payload"].Value = draft.Payload.ToArray();
        command.Parameters["$targetAddress"].Value = (object?)draft.TargetAddress ?? DBNull.Value;
        command.Parameters["$qos"].Value = (object?)draft.QualityOfService ?? DBNull.Value;
        command.Parameters["$headers"].Value = draft.Headers is null ? DBNull.Value : JsonSerializer.Serialize(draft.Headers);
        command.Parameters["$fingerprint"].Value = draft.Fingerprint;
        command.Parameters["$fingerprintSummary"].Value = draft.FingerprintSummary;
        command.Parameters["$createdAt"].Value = createdAt;
        command.Parameters["$bytes"].Value = draft.ByteSize;
        command.Parameters["$protocolMetadata"].Value = (object?)draft.ProtocolMetadata ?? DBNull.Value;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>为写者准备插入语句，参数在 <see cref="Insert"/> 中逐条赋值。</summary>
    public static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delivery (delivery_id, connection_id, source_message_id, device_key, route_id, node_label, revision, payload,
                target_address, qos, headers, fingerprint, fingerprint_summary, created_at, byte_size, protocol_metadata)
            VALUES ($deliveryId, $connectionId, $sourceMessageId, $deviceKey, $routeId, $nodeLabel, $revision, $payload,
                $targetAddress, $qos, $headers, $fingerprint, $fingerprintSummary, $createdAt, $bytes, $protocolMetadata)
            RETURNING seq;
            """;
        foreach (var name in new[]
        {
            "$deliveryId", "$connectionId", "$sourceMessageId", "$deviceKey", "$routeId", "$nodeLabel", "$revision", "$payload",
            "$targetAddress", "$qos", "$headers", "$fingerprint", "$fingerprintSummary", "$createdAt", "$bytes", "$protocolMetadata",
        })
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        return command;
    }

    /// <summary>为写者准备按 seq 删除的语句。</summary>
    public static SqliteCommand CreateDeleteCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM delivery WHERE seq = $seq;";
        command.Parameters.Add(new SqliteParameter { ParameterName = "$seq" });
        return command;
    }

    private static StoredDelivery ReadRecord(SqliteDataReader reader) => new(
        Seq: reader.GetInt64(0),
        DeliveryId: Guid.Parse(reader.GetString(1)),
        ConnectionId: reader.GetInt64(2),
        SourceMessageId: Guid.Parse(reader.GetString(3)),
        DeviceKey: reader.GetString(4),
        RouteId: reader.IsDBNull(5) ? null : reader.GetInt64(5),
        NodeLabel: reader.IsDBNull(6) ? null : reader.GetString(6),
        Revision: reader.GetInt64(7),
        Payload: (byte[])reader.GetValue(8),
        TargetAddress: reader.IsDBNull(9) ? null : reader.GetString(9),
        QualityOfService: reader.IsDBNull(10) ? null : reader.GetInt32(10),
        Headers: reader.IsDBNull(11)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(11))?.ToImmutableSortedDictionary(StringComparer.Ordinal),
        Fingerprint: reader.GetString(12),
        FingerprintSummary: reader.GetString(13),
        CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        FailureCount: reader.GetInt32(15),
        State: reader.GetString(16) switch
        {
            "pending" => DeliveryState.Pending,
            "failed" => DeliveryState.Failed,
            "quarantined" => DeliveryState.Quarantined,
            var other => throw new DeliveryStoreException($"未知的投递状态 {other}。"),
        },
        QuarantineReason: reader.IsDBNull(17) ? null : reader.GetString(17),
        LastError: reader.IsDBNull(18) ? null : reader.GetString(18),
        MigrationId: reader.IsDBNull(19) ? null : reader.GetInt64(19),
        FailedAt: reader.IsDBNull(20) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(20)),
        ByteSize: reader.GetInt64(21),
        ClockAnomaly: reader.GetInt64(22) != 0,
        ProtocolMetadata: reader.IsDBNull(23) ? null : reader.GetString(23),
        LastRetriedAt: reader.IsDBNull(24) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(24)));

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static (string JournalMode, long Synchronous) ApplySettings(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = ApplySettingsSql;
            command.ExecuteNonQuery();
        }

        var journalMode = Convert.ToString(Scalar(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture) ?? string.Empty;
        var synchronous = Convert.ToInt64(Scalar(connection, "PRAGMA synchronous;"), CultureInfo.InvariantCulture);
        var busyTimeout = Convert.ToInt64(Scalar(connection, "PRAGMA busy_timeout;"), CultureInfo.InvariantCulture);
        if (!string.Equals(journalMode, ExpectedJournalMode, StringComparison.OrdinalIgnoreCase)
            || synchronous != SynchronousFull
            || busyTimeout != BusyTimeoutMilliseconds)
        {
            throw new DeliveryStoreException(
                $"发送缓存连接设置未生效：journal_mode={journalMode}，synchronous={synchronous}，busy_timeout={busyTimeout}；" +
                $"应为 {ExpectedJournalMode}、{SynchronousFull}（FULL）、{BusyTimeoutMilliseconds}。");
        }

        return (journalMode, synchronous);
    }

    private static int Migrate(SqliteConnection connection)
    {
        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (current > Migrations.Length)
        {
            throw new DeliveryStoreException($"发送缓存的表结构版本为 {current}，由更新版本的程序创建；本程序支持到 {Migrations.Length}，不支持降级。");
        }

        for (var version = current + 1; version <= Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Migrations[version - 1] + $"\nPRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        return Migrations.Length;
    }

    private string EnsureMeta(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO queue_meta (id, config_store_id, created_at) VALUES (1, $configStoreId, $createdAt);";
        command.Parameters.AddWithValue("$configStoreId", _options.ConfigStoreId);
        command.Parameters.AddWithValue("$createdAt", _time.GetUtcNow().ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "SELECT config_store_id FROM queue_meta WHERE id = 1;";
        var stored = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        if (stored != _options.ConfigStoreId)
        {
            QuarantineWhere(connection, transaction, "state IN ('pending', 'failed')", DeliveryQuarantineReasons.ConfigStoreChanged);
            command.CommandText = "UPDATE delivery SET quarantine_reason = 'configStoreChanged' WHERE state = 'quarantined'";
            command.ExecuteNonQuery();
            command.Parameters.AddWithValue("$configStoreId", _options.ConfigStoreId);
            command.CommandText = "UPDATE queue_meta SET config_store_id = $configStoreId WHERE id = 1";
            command.ExecuteNonQuery();
            stored = _options.ConfigStoreId;
        }

        transaction.Commit();
        return stored;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void CreateFileForOwnerOnly(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var file = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            // 并发创建时由另一方先建成，沿用其文件。
        }
    }
}

/// <summary>发送缓存不满足打开条件：SQLite 版本过低、持久化设置未生效、表结构来自更新版本的程序等。</summary>
public sealed class DeliveryStoreException : Exception
{
    public DeliveryStoreException()
    {
    }

    public DeliveryStoreException(string message)
        : base(message)
    {
    }

    public DeliveryStoreException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
