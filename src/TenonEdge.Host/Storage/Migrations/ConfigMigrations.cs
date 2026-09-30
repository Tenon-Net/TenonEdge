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
    ];
}
