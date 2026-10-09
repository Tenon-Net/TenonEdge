namespace TenonEdge.Host.Storage.Migrations;

/// <summary>
/// 本程序的配置库迁移定义，按版本号排列（总设计 §2.3：只做显式的建表与追加，不用 SqlSugar CodeFirst 自动改表）。
/// 时间一律以 INTEGER 保存 UTC 的 Unix 毫秒数，不依赖 ORM 对日期类型的文本格式。
/// </summary>
internal static class ConfigMigrations
{
    public static IReadOnlyList<ConfigMigration> All { get; } =
    [
        new(1, "system_state", [
            """
            CREATE TABLE system_state (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1)
            )
            """,
            "INSERT INTO system_state (id) VALUES (1)",
        ]),

        // T0-06：网关身份与内部 ID 下限（总设计 §10.1、§10.2、§10.4）。两个下限都表示“新分配的值必须大于它”。
        new(2, "gateway_identity", [
            "ALTER TABLE system_state ADD COLUMN node_id TEXT",
            "ALTER TABLE system_state ADD COLUMN config_store_id TEXT",
            "ALTER TABLE system_state ADD COLUMN id_floor INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE system_state ADD COLUMN revision_floor INTEGER NOT NULL DEFAULT 0",
        ]),

        // T0-07：首次设置状态、本地账号与审计记录（总设计 §3.3、§10.2、§10.3）。
        new(3, "accounts_and_audit", [
            "ALTER TABLE system_state ADD COLUMN setup_state TEXT NOT NULL DEFAULT 'uninitialized' CHECK (setup_state IN ('uninitialized', 'initialized', 'resetPending'))",
            "ALTER TABLE system_state ADD COLUMN setup_reset_account_id INTEGER",
            "ALTER TABLE system_state ADD COLUMN setup_completed_at INTEGER",
            "ALTER TABLE system_state ADD COLUMN setup_source_ip TEXT",
            "ALTER TABLE system_state ADD COLUMN gateway_name TEXT",
            """
            CREATE TABLE user_account (
                id INTEGER NOT NULL PRIMARY KEY,
                account_name TEXT NOT NULL,
                normalized_account_name TEXT NOT NULL,
                display_name TEXT,
                password_hash TEXT,
                must_change_password INTEGER NOT NULL DEFAULT 0,
                role TEXT NOT NULL,
                security_stamp TEXT NOT NULL,
                failed_login_count INTEGER NOT NULL DEFAULT 0,
                lockout_end INTEGER,
                last_login_at INTEGER,
                last_login_ip TEXT,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_at INTEGER NOT NULL
            )
            """,
            "CREATE UNIQUE INDEX ux_user_account_normalized_account_name ON user_account (normalized_account_name)",
            """
            CREATE TABLE audit_log (
                id INTEGER NOT NULL PRIMARY KEY,
                occurred_at INTEGER NOT NULL,
                account_name TEXT,
                token_name TEXT,
                source_ip TEXT,
                action TEXT NOT NULL,
                target TEXT,
                result TEXT NOT NULL,
                detail TEXT
            )
            """,
            "CREATE INDEX ix_audit_log_occurred_at ON audit_log (occurred_at)",
        ]),

        // T0-08：会话代次（总设计 §3.3）。开启“只允许 HTTPS”或恢复备份时加一，使全部会话失效。
        new(4, "session_generation", [
            "ALTER TABLE system_state ADD COLUMN session_generation INTEGER NOT NULL DEFAULT 1",
        ]),

        // T0-09：运行事件的最小存储（总设计 §3.3、§10.2）。只记状态变化，同一对象的同类事件合并计数；保留 7 天、最多 1 万条。
        new(5, "runtime_events", [
            """
            CREATE TABLE runtime_event (
                id INTEGER NOT NULL PRIMARY KEY,
                first_occurred_at INTEGER NOT NULL,
                last_occurred_at INTEGER NOT NULL,
                level TEXT NOT NULL CHECK (level IN ('info', 'warning', 'error')),
                type TEXT NOT NULL,
                target TEXT NOT NULL DEFAULT '',
                message TEXT NOT NULL,
                occurrence_count INTEGER NOT NULL DEFAULT 1
            )
            """,
            "CREATE INDEX ix_runtime_event_last_occurred_at ON runtime_event (last_occurred_at)",
            "CREATE INDEX ix_runtime_event_type_target ON runtime_event (type, target, first_occurred_at)",
        ]),

        // T1-01：运行快照与有效版本的最小元数据（总设计 §7.4、§10.2 RuntimeSnapshot）。版本号单调递增、不复用：
        // 每次分配都把 revision_floor 推进到新版本号。running_revision 是“运行中”标记：激活前写入，按停机顺序正常退出时清除；
        // 启动时仍在，说明上次非正常退出（启动保护）。seed_hash 是最近一次导入的种子快照的内容哈希，种子不变时不重复导入。
        new(6, "runtime_snapshots", [
            """
            CREATE TABLE runtime_snapshot (
                revision INTEGER NOT NULL PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                origin TEXT NOT NULL,
                origin_detail TEXT,
                state TEXT NOT NULL CHECK (state IN ('pending', 'effective', 'history', 'notEffective')),
                not_effective_reason TEXT,
                is_stable INTEGER NOT NULL DEFAULT 0,
                stable_at INTEGER,
                abnormal_exit_count INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                created_by TEXT NOT NULL,
                activated_at INTEGER
            )
            """,
            "ALTER TABLE system_state ADD COLUMN effective_revision INTEGER",
            "ALTER TABLE system_state ADD COLUMN previous_revision INTEGER",
            "ALTER TABLE system_state ADD COLUMN pending_revision INTEGER",
            "ALTER TABLE system_state ADD COLUMN running_revision INTEGER",
            "ALTER TABLE system_state ADD COLUMN seed_hash TEXT",
        ]),
        new(7, "mqtt_credentials", [
            """
            CREATE TABLE mqtt_credential (
                connection_id INTEGER NOT NULL PRIMARY KEY,
                recipient TEXT NOT NULL,
                username TEXT NOT NULL,
                protected_password TEXT,
                account_marker TEXT NOT NULL,
                version TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(8, "output_control", [
            """
            CREATE TABLE output_control (
                connection_id INTEGER NOT NULL PRIMARY KEY,
                manually_paused INTEGER NOT NULL,
                version TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(9, "outbound_policy", [
            """
            CREATE TABLE outbound_policy (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                version TEXT NOT NULL,
                networks TEXT NOT NULL,
                local_ports TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(10, "http_credentials", [
            """
            CREATE TABLE http_credential (
                connection_id INTEGER NOT NULL PRIMARY KEY,
                recipient TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK (kind BETWEEN 0 AND 3),
                username TEXT,
                name TEXT,
                protected_secret TEXT,
                account_marker TEXT NOT NULL,
                version TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        // T2-03：先记订阅意图，再发 SUBSCRIBE；网络应答与本地提交不宣称原子性。
        new(11, "mqtt_subscriptions", [
            """
            CREATE TABLE mqtt_subscription (
                recipient TEXT NOT NULL,
                client_id TEXT NOT NULL,
                topic_filter TEXT NOT NULL,
                qos INTEGER NOT NULL CHECK (qos IN (0, 1)),
                confirmed INTEGER NOT NULL CHECK (confirmed IN (0, 1)),
                PRIMARY KEY (recipient, client_id, topic_filter)
            )
            """,
        ]),
        new(12, "configuration_draft", [
            """
            CREATE TABLE configuration_draft (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                version TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
            "INSERT INTO configuration_draft (id, version, updated_at) VALUES (1, '0', 0)",
            """
            CREATE TABLE connection_draft (
                id INTEGER NOT NULL PRIMARY KEY,
                business_key TEXT NOT NULL UNIQUE,
                content TEXT NOT NULL
            )
            """,
        ]),
        new(13, "device_draft", [
            """
            CREATE TABLE device_draft (
                id INTEGER NOT NULL PRIMARY KEY,
                business_key TEXT NOT NULL UNIQUE,
                connection_id INTEGER REFERENCES connection_draft(id) ON DELETE RESTRICT,
                content TEXT NOT NULL
            )
            """,
            "CREATE INDEX ix_device_draft_connection ON device_draft (connection_id)",
        ]),
    ];
}
