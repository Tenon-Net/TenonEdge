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
        new(14, "input_draft", [
            """
            CREATE TABLE input_draft (
                id INTEGER NOT NULL PRIMARY KEY,
                device_id INTEGER NOT NULL REFERENCES device_draft(id) ON DELETE RESTRICT,
                business_key TEXT NOT NULL,
                content TEXT NOT NULL,
                UNIQUE (device_id, business_key)
            )
            """,
        ]),
        new(15, "route_draft", [
            """
            CREATE TABLE route_draft (
                id INTEGER NOT NULL PRIMARY KEY,
                business_key TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT,
                enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                node_count INTEGER NOT NULL,
                updated_by TEXT,
                updated_at INTEGER NOT NULL,
                graph TEXT NOT NULL
            )
            """,
            """
            CREATE TABLE route_draft_reference (
                route_id INTEGER NOT NULL REFERENCES route_draft(id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK (kind IN ('input', 'connection')),
                target_id INTEGER NOT NULL,
                PRIMARY KEY (route_id, kind, target_id)
            )
            """,
            "CREATE INDEX ix_route_draft_reference_target ON route_draft_reference (kind, target_id)",
        ]),
        new(16, "runtime_change_description", [
            "ALTER TABLE runtime_snapshot ADD COLUMN change_description TEXT CHECK (change_description IS NULL OR length(change_description) <= 1000)",
        ]),
        new(17, "configuration_edit", [
            """
            CREATE TABLE configuration_edit (
                kind TEXT NOT NULL CHECK(kind IN ('connection', 'device', 'input', 'route')),
                object_id INTEGER NOT NULL,
                stamp TEXT NOT NULL,
                updated_by TEXT,
                updated_at INTEGER,
                deleted INTEGER NOT NULL CHECK(deleted IN (0, 1)),
                retained INTEGER NOT NULL CHECK(retained IN (0, 1)),
                PRIMARY KEY(kind, object_id)
            )
            """,
            "INSERT INTO configuration_edit SELECT 'connection', id, '', NULL, NULL, 0, 1 FROM connection_draft",
            "INSERT INTO configuration_edit SELECT 'device', id, '', NULL, NULL, 0, 1 FROM device_draft",
            "INSERT INTO configuration_edit SELECT 'input', id, '', NULL, NULL, 0, 1 FROM input_draft",
            "INSERT INTO configuration_edit SELECT 'route', id, '', updated_by, CASE WHEN updated_at > 0 THEN updated_at END, 0, 1 FROM route_draft",
        ]),
        new(18, "delivery_migration", [
            """
            CREATE TABLE delivery_migration (
                operation_id TEXT NOT NULL PRIMARY KEY,
                config_store_id TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                mode TEXT NOT NULL CHECK(mode IN ('selected', 'oldTarget')),
                revision INTEGER NOT NULL,
                source_connection_id INTEGER NOT NULL,
                target_connection_id INTEGER NOT NULL,
                source_summary TEXT NOT NULL,
                target_summary TEXT NOT NULL,
                requested_records INTEGER,
                account_name TEXT NOT NULL,
                source_ip TEXT,
                started_at INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('started', 'completed', 'rejected', 'interrupted')),
                migration_id INTEGER,
                affected_records INTEGER,
                completed_at INTEGER,
                error_code TEXT
            )
            """,
            "CREATE INDEX ix_delivery_migration_status_started ON delivery_migration (status, started_at)",
        ]),
        new(19, "delivery_migration_intent", [
            """
            CREATE TABLE delivery_migration_intent (
                intent_id TEXT NOT NULL PRIMARY KEY,
                config_store_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                source_revision INTEGER NOT NULL,
                connection_id INTEGER NOT NULL,
                protocol TEXT NOT NULL CHECK(protocol IN ('mqtt', 'http')),
                old_fingerprint TEXT NOT NULL,
                source_summary TEXT NOT NULL,
                target_recipient TEXT NOT NULL,
                account_name TEXT NOT NULL,
                source_ip TEXT,
                registered_at INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('waiting', 'rejected', 'completed')),
                last_operation_id TEXT,
                UNIQUE(revision, connection_id)
            )
            """,
            "CREATE INDEX ix_delivery_migration_intent_waiting ON delivery_migration_intent(status, revision, connection_id)",
        ]),
        new(20, "delivery_migration_intent_execution", [
            "ALTER TABLE delivery_migration ADD COLUMN intent_id TEXT REFERENCES delivery_migration_intent(intent_id)",
            "CREATE INDEX ix_delivery_migration_intent_attempt ON delivery_migration(intent_id, started_at)",
            "ALTER TABLE delivery_migration_intent ADD COLUMN target_fingerprint TEXT",
            "ALTER TABLE delivery_migration_intent ADD COLUMN target_summary TEXT",
            "CREATE INDEX ix_delivery_migration_intent_order ON delivery_migration_intent(config_store_id, connection_id, revision) WHERE status = 'waiting'",
        ]),
        new(21, "delivery_migration_audit_token", [
            "ALTER TABLE delivery_migration ADD COLUMN token_name TEXT",
            "ALTER TABLE delivery_migration_intent ADD COLUMN token_name TEXT",
        ]),
        new(22, "api_token", [
            """
            CREATE TABLE api_token (
                id INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                role TEXT NOT NULL CHECK(role IN ('Administrator', 'Configurator', 'Viewer')),
                secret_hash BLOB NOT NULL CHECK(length(secret_hash) = 32),
                created_at INTEGER NOT NULL,
                expires_at INTEGER,
                revoked_at INTEGER,
                last_used_at INTEGER,
                creator_user_id INTEGER NOT NULL,
                creator_account_name TEXT NOT NULL,
                created_by_token_name TEXT
            )
            """,
            "CREATE INDEX ix_api_token_creator ON api_token(creator_user_id, id) WHERE revoked_at IS NULL",
        ]),
        new(23, "runtime_parameters", [
            """
            CREATE TABLE runtime_parameters (
                id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                version TEXT NOT NULL,
                audit_retention_days INTEGER NOT NULL CHECK(audit_retention_days BETWEEN 180 AND 36500),
                preview_global_limit INTEGER NOT NULL CHECK(preview_global_limit BETWEEN 1 AND 64),
                preview_session_limit INTEGER NOT NULL CHECK(preview_session_limit BETWEEN 1 AND preview_global_limit),
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(24, "queue_parameters", [
            """
            CREATE TABLE queue_parameters (
                id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                version TEXT NOT NULL,
                values_json TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(25, "trusted_proxy_rules", [
            """
            CREATE TABLE trusted_proxy_rules (
                id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                version TEXT NOT NULL,
                networks_json TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """,
        ]),
        new(26, "debug_cache_parameters", [
            "ALTER TABLE runtime_parameters ADD COLUMN debug_object_bytes INTEGER NOT NULL DEFAULT 1048576 CHECK(debug_object_bytes BETWEEN 67584 AND 67108864)",
            "ALTER TABLE runtime_parameters ADD COLUMN debug_global_bytes INTEGER NOT NULL DEFAULT 33554432 CHECK(debug_global_bytes BETWEEN debug_object_bytes AND 1073741824)",
            "ALTER TABLE runtime_parameters ADD COLUMN debug_object_records INTEGER NOT NULL DEFAULT 100 CHECK(debug_object_records BETWEEN 1 AND 100)",
        ]),
        new(27, "in_flight_parameter", [
            "ALTER TABLE runtime_parameters ADD COLUMN in_flight_bytes INTEGER NOT NULL DEFAULT 67108864 CHECK(in_flight_bytes BETWEEN 16777216 AND 1073741824)",
        ]),
        new(28, "input_channel_parameter", [
            "ALTER TABLE runtime_parameters ADD COLUMN input_channel_capacity INTEGER NOT NULL DEFAULT 100 CHECK(input_channel_capacity BETWEEN 1 AND 10000)",
        ]),
        new(29, "plan_scale_parameters", [
            "ALTER TABLE runtime_parameters ADD COLUMN plan_sources INTEGER NOT NULL DEFAULT 2000 CHECK(plan_sources BETWEEN 1 AND 2000)",
            "ALTER TABLE runtime_parameters ADD COLUMN plan_connections INTEGER NOT NULL DEFAULT 500 CHECK(plan_connections BETWEEN 1 AND 500)",
            "ALTER TABLE runtime_parameters ADD COLUMN plan_modbus_connections INTEGER NOT NULL DEFAULT 256 CHECK(plan_modbus_connections BETWEEN 1 AND 256)",
            "ALTER TABLE runtime_parameters ADD COLUMN plan_paths_per_source INTEGER NOT NULL DEFAULT 32 CHECK(plan_paths_per_source BETWEEN 1 AND 32)",
            "ALTER TABLE runtime_parameters ADD COLUMN plan_total_paths INTEGER NOT NULL DEFAULT 20000 CHECK(plan_total_paths BETWEEN 1 AND 20000)",
        ]),
        new(30, "import_credential_requirements", [
            """
            CREATE TABLE configuration_credential_requirement (
                connection_id INTEGER NOT NULL PRIMARY KEY,
                account_identity TEXT NOT NULL CHECK(length(account_identity) = 71),
                reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64)
            )
            """,
        ]),
    ];
}
