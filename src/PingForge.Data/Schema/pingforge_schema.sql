-- PingForge: схема локального хранилища. Версия 5.
-- ВАЖНО: PRAGMA journal_mode сохраняется в файле БД, остальные прагмы (foreign_keys,
-- busy_timeout, synchronous) — уровня соединения и выставляются в SqliteDatabaseInitializer.

CREATE TABLE IF NOT EXISTS schema_version_history (
    version_number INTEGER PRIMARY KEY NOT NULL,
    installed_utc TEXT NOT NULL,
    notes TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS app_settings (
    setting_key TEXT PRIMARY KEY NOT NULL,
    setting_value TEXT NOT NULL,
    value_type TEXT NOT NULL CHECK (value_type IN ('String', 'Boolean', 'Integer', 'Double', 'Json')),
    updated_utc TEXT NOT NULL
);

-- Снапшоты состояния системы перед любым вмешательством.
CREATE TABLE IF NOT EXISTS tweak_snapshots (
    snapshot_id TEXT PRIMARY KEY NOT NULL,
    created_utc TEXT NOT NULL,
    snapshot_label TEXT NOT NULL,
    selected_profile TEXT NOT NULL,
    restore_point_status TEXT NOT NULL CHECK (restore_point_status IN (
        'CreatedSuccessfully', 'SkippedFrequencyLimit24h', 'SystemProtectionDisabled', 'NotRequested', 'FailedWithError'
    )),
    payload_sha256 TEXT NOT NULL,
    rollback_status TEXT NULL CHECK (rollback_status IN ('FullyRolledBack', 'PartiallyRolledBack', 'RequiresManualVerification', 'Failed')),
    was_rolled_back INTEGER NOT NULL DEFAULT 0 CHECK (was_rolled_back IN (0, 1)),
    rolled_back_utc TEXT NULL
);

CREATE TABLE IF NOT EXISTS tweak_snapshot_items (
    item_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id TEXT NOT NULL,
    tweak_code TEXT NOT NULL,
    mechanism_kind TEXT NOT NULL CHECK (mechanism_kind IN (
        'Registry', 'WmiNicAdvancedProperty', 'WmiNicPowerManagement',
        'NetshCommand', 'PowerCfgCommand', 'QoSPolicyRegistry',
        'DnsResolverConfig', 'MtuSubinterface'
    )),
    resource_locator TEXT NOT NULL,
    -- Отдельные колонки вместо хака с маркерами внутри resource_locator.
    adapter_interface_guid TEXT NULL,
    adapter_display_name TEXT NULL,
    existed_before INTEGER NOT NULL CHECK (existed_before IN (0, 1)),
    captured_raw_value TEXT NULL,
    value_data_type TEXT NOT NULL CHECK (value_data_type IN (
        'DWord', 'QWord', 'String', 'MultiString', 'Binary', 'CimString', 'CommandState', 'None'
    )),
    planned_value TEXT NOT NULL,
    -- Человекочитаемое пояснение, почему выбрано именно это целевое значение (важно для вычисляемых твиков).
    planned_value_reason TEXT NULL,
    FOREIGN KEY (snapshot_id) REFERENCES tweak_snapshots(snapshot_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_snapshot_items_parent ON tweak_snapshot_items(snapshot_id);
CREATE INDEX IF NOT EXISTS ix_snapshot_items_code ON tweak_snapshot_items(tweak_code);
CREATE INDEX IF NOT EXISTS ix_snapshot_items_adapter ON tweak_snapshot_items(adapter_interface_guid);

-- Матрица поддерживаемости: какие твики реально работают на ЭТОЙ машине/драйвере.
CREATE TABLE IF NOT EXISTS tweak_support_matrix (
    tweak_code TEXT PRIMARY KEY NOT NULL,
    target_description TEXT NOT NULL,
    is_supported INTEGER NOT NULL CHECK (is_supported IN (0, 1)),
    requires_manual_verification INTEGER NOT NULL DEFAULT 0 CHECK (requires_manual_verification IN (0, 1)),
    support_reason_ru TEXT NOT NULL,
    current_live_value TEXT NULL,
    evaluated_utc TEXT NOT NULL
);

-- Журнал предзаписи: запись ДО изменения системы, снятие при старте после падения.
CREATE TABLE IF NOT EXISTS pending_operations_journal (
    op_id TEXT PRIMARY KEY NOT NULL,
    snapshot_id TEXT NULL,
    op_category TEXT NOT NULL CHECK (op_category IN (
        'TWEAK_APPLY', 'ROUTE_ADD', 'WINDIVERT_START', 'TUNNEL_UP', 'DNS_SET'
    )),
    target_key TEXT NOT NULL,
    undo_descriptor_json TEXT NOT NULL,
    op_state TEXT NOT NULL CHECK (op_state IN ('PENDING', 'COMMITTED', 'ROLLED_BACK', 'FAILED')),
    started_utc TEXT NOT NULL,
    finished_utc TEXT NULL,
    failure_reason TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_pending_ops_state ON pending_operations_journal(op_state);

-- Журнал изменений системы: что, когда, было → стало, что реально прочитано из системы.
CREATE TABLE IF NOT EXISTS system_modification_log (
    entry_id INTEGER PRIMARY KEY AUTOINCREMENT,
    logged_utc TEXT NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN (
        'APPLY', 'ROLLBACK', 'VERIFY_FAIL', 'SKIPPED_NO_OP', 'SKIPPED_UNPARSABLE',
        'CRASH_RECOVERY', 'EMERGENCY_RESET', 'FAILOVER'
    )),
    tweak_code TEXT NULL,
    tweak_title TEXT NOT NULL,
    resource_locator TEXT NOT NULL,
    previous_value_text TEXT NOT NULL,
    applied_value_text TEXT NOT NULL,
    verified_actual_text TEXT NOT NULL,
    succeeded INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
    reboot_needed INTEGER NOT NULL DEFAULT 0 CHECK (reboot_needed IN (0, 1)),
    explanation TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_mod_log_time ON system_modification_log(logged_utc DESC);
CREATE INDEX IF NOT EXISTS ix_mod_log_tweak_code ON system_modification_log(tweak_code);

-- Результаты сканера: что уже оптимально, что стоит изменить, к чему не прикасаться, найденные помехи.
CREATE TABLE IF NOT EXISTS system_audit_findings (
    finding_id INTEGER PRIMARY KEY AUTOINCREMENT,
    scanned_utc TEXT NOT NULL,
    finding_category TEXT NOT NULL CHECK (finding_category IN (
        'OPTIMAL', 'RECOMMEND_CHANGE', 'OPTIONAL', 'DO_NOT_TOUCH', 'CONFLICT_WARNING', 'UNRESOLVED'
    )),
    code TEXT NOT NULL,
    title_ru TEXT NOT NULL,
    details_ru TEXT NOT NULL,
    severity TEXT NOT NULL CHECK (severity IN ('Low', 'Medium', 'High'))
);

CREATE INDEX IF NOT EXISTS ix_audit_findings_category ON system_audit_findings(finding_category);

CREATE TABLE IF NOT EXISTS user_tweak_presets (
    preset_id TEXT PRIMARY KEY NOT NULL,
    preset_name TEXT NOT NULL UNIQUE,
    summary TEXT NOT NULL,
    enabled_tweak_codes_json TEXT NOT NULL,
    saved_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS game_profiles (
    profile_id TEXT PRIMARY KEY NOT NULL,
    exe_name TEXT NOT NULL UNIQUE,
    display_title TEXT NOT NULL,
    process_path_pattern TEXT NULL,
    protocol_kind TEXT NOT NULL CHECK (protocol_kind IN ('UDP', 'TCP', 'BOTH')),
    default_ports_csv TEXT NOT NULL,
    known_subnets_csv TEXT NOT NULL,
    fallback_probe_endpoint TEXT NULL,
    anti_cheat_name TEXT NOT NULL,
    anti_cheat_status TEXT NOT NULL CHECK (anti_cheat_status IN ('VerifiedSafe', 'Untested', 'KnownKernelConflict')),
    anti_cheat_warning_ru TEXT NOT NULL,
    is_user_custom INTEGER NOT NULL DEFAULT 0 CHECK (is_user_custom IN (0, 1)),
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS game_process_sessions (
    game_session_id TEXT PRIMARY KEY NOT NULL,
    profile_id TEXT NOT NULL,
    exe_name TEXT NOT NULL,
    process_id INTEGER NOT NULL,
    discovered_live_endpoint TEXT NOT NULL,
    is_fallback_endpoint INTEGER NOT NULL DEFAULT 0 CHECK (is_fallback_endpoint IN (0, 1)),
    probe_method_used TEXT NULL,
    started_utc TEXT NOT NULL,
    ended_utc TEXT NULL,
    avg_traffic_mb_per_min REAL NOT NULL DEFAULT 0,
    FOREIGN KEY (profile_id) REFERENCES game_profiles(profile_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS user_relay_nodes (
    node_id TEXT PRIMARY KEY NOT NULL,
    alias TEXT NOT NULL,
    region_tag TEXT NOT NULL,
    endpoint_ip TEXT NOT NULL,
    endpoint_port INTEGER NOT NULL,
    tunnel_protocol TEXT NOT NULL CHECK (tunnel_protocol IN ('WireGuardExternalCli', 'NativeUdpRelay')),
    server_public_key TEXT NOT NULL,
    protected_client_private_key TEXT NOT NULL,
    key_protection_kind TEXT NOT NULL CHECK (key_protection_kind IN ('DPAPI_LocalMachine', 'NotConfigured')),
    client_tunnel_cidr TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    added_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS relay_node_health_history (
    health_id INTEGER PRIMARY KEY AUTOINCREMENT,
    node_id TEXT NOT NULL,
    checked_utc TEXT NOT NULL,
    is_reachable INTEGER NOT NULL CHECK (is_reachable IN (0, 1)),
    rtt_avg_ms REAL NULL,
    jitter_ms REAL NULL,
    loss_pct REAL NULL,
    estimated_load_pct REAL NULL,
    computed_score REAL NULL,
    FOREIGN KEY (node_id) REFERENCES user_relay_nodes(node_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_relay_health_node_time ON relay_node_health_history(node_id, checked_utc DESC);

-- Сессии оптимизации (задача J): базовые метрики «до» и метрики «после», действия сессии,
-- честный вердикт и пометка «снялось / не снялось». Незавершённая сессия остаётся с
-- is_completed = 0 и outcome = 'NotFinished': выдуманного улучшения у неё нет.
CREATE TABLE IF NOT EXISTS route_optimization_sessions (
    session_id TEXT PRIMARY KEY NOT NULL,
    game_session_id TEXT NULL,
    process_exe_name TEXT NOT NULL,
    game_title_ru TEXT NOT NULL DEFAULT '',
    remote_game_endpoint TEXT NOT NULL,
    selected_mode TEXT NOT NULL,
    relay_node_id TEXT NULL,
    baseline_probe_method TEXT NOT NULL,
    optimized_probe_method TEXT NULL,
    started_utc TEXT NOT NULL,
    stopped_utc TEXT NULL,
    baseline_rtt_avg REAL NULL,
    baseline_rtt_p95 REAL NULL,
    baseline_jitter REAL NULL,
    baseline_loss_pct REAL NULL,
    baseline_stability REAL NULL,
    optimized_rtt_avg REAL NULL,
    optimized_rtt_p95 REAL NULL,
    optimized_jitter REAL NULL,
    optimized_loss_pct REAL NULL,
    optimized_stability REAL NULL,
    is_measurable INTEGER NOT NULL DEFAULT 1 CHECK (is_measurable IN (0, 1)),
    degradation_alert_triggered INTEGER NOT NULL DEFAULT 0 CHECK (degradation_alert_triggered IN (0, 1)),
    applied_actions_json TEXT NOT NULL DEFAULT '[]',
    rollback_state TEXT NOT NULL DEFAULT 'Unknown' CHECK (rollback_state IN (
        'EverythingReverted', 'PartiallyReverted', 'NotReverted', 'Unknown'
    )),
    outcome TEXT NOT NULL DEFAULT 'NotFinished' CHECK (outcome IN ('Completed', 'NotFinished', 'NoGame')),
    is_completed INTEGER NOT NULL DEFAULT 0 CHECK (is_completed IN (0, 1)),
    verdict_comment TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_route_sessions_exe ON route_optimization_sessions(process_exe_name);
CREATE INDEX IF NOT EXISTS ix_route_sessions_endpoint ON route_optimization_sessions(remote_game_endpoint, started_utc DESC);

CREATE TABLE IF NOT EXISTS route_probe_timeseries (
    point_id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NULL,
    sampled_utc TEXT NOT NULL,
    destination_host TEXT NOT NULL,
    path_name TEXT NOT NULL,
    probe_method TEXT NOT NULL,
    rtt_avg REAL NULL,
    rtt_min REAL NULL,
    rtt_max REAL NULL,
    rtt_p95 REAL NULL,
    jitter REAL NULL,
    loss_pct REAL NULL,
    hops_count INTEGER NOT NULL,
    route_score REAL NULL
);

CREATE INDEX IF NOT EXISTS ix_probe_timeseries_utc ON route_probe_timeseries(sampled_utc DESC);
