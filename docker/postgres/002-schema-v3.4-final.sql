-- =============================================================================
-- YoTelematics v3.4-FINAL Master Schema, Hypertables, Ledger Trigger & Forced RLS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "postgis";
CREATE EXTENSION IF NOT EXISTS "timescaledb";
CREATE EXTENSION IF NOT EXISTS "ltree";
CREATE EXTENSION IF NOT EXISTS "btree_gist";

-- 1. DATA PROTECTION KEY RING
CREATE TABLE IF NOT EXISTS data_protection_keys (
    id SERIAL PRIMARY KEY,
    friendly_name TEXT,
    xml TEXT NOT NULL
);

-- 2. TENANTS & HIERARCHY
CREATE TABLE IF NOT EXISTS tenants (
    id UUID PRIMARY KEY,
    parent_tenant_id UUID REFERENCES tenants(id),
    hierarchy_path LTREE NOT NULL,
    name VARCHAR(120) NOT NULL,
    slug VARCHAR(64) UNIQUE NOT NULL,
    custom_domain VARCHAR(120) UNIQUE,
    tls_status VARCHAR(24) NOT NULL DEFAULT 'None',
    default_locale VARCHAR(10) NOT NULL DEFAULT 'bn-BD',
    timezone VARCHAR(50) NOT NULL DEFAULT 'Asia/Dhaka',
    theme_json JSONB NOT NULL DEFAULT '{}',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_tenants_hierarchy_gist ON tenants USING GIST (hierarchy_path);

CREATE TABLE IF NOT EXISTS tenant_holidays (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    holiday_date DATE NOT NULL,
    description VARCHAR(100) NOT NULL,
    UNIQUE(tenant_id, holiday_date)
);

CREATE TABLE IF NOT EXISTS tenant_provider_configs (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    provider_type VARCHAR(24) NOT NULL,
    encrypted_config_bytes BYTEA NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE(tenant_id, provider_type)
);

-- 3. ORGANIZATIONS, BRANCHES, USERS (Nullable Password), PUSH & OTP (with tenant_id)
CREATE TABLE IF NOT EXISTS organizations (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    name VARCHAR(120) NOT NULL
);

CREATE TABLE IF NOT EXISTS branches (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    organization_id UUID NOT NULL REFERENCES organizations(id),
    name VARCHAR(120) NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    parent_dealer_id UUID REFERENCES users(id),
    branch_id UUID REFERENCES branches(id),
    phone VARCHAR(24) NOT NULL,
    password_hash TEXT,
    security_pin_hash TEXT,
    failed_pin_attempts SMALLINT NOT NULL DEFAULT 0,
    pin_locked_until_utc TIMESTAMPTZ,
    role VARCHAR(32) NOT NULL,
    locale VARCHAR(10) NOT NULL DEFAULT 'bn-BD',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, phone)
);

CREATE TABLE IF NOT EXISTS user_push_tokens (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    fcm_token TEXT NOT NULL,
    platform VARCHAR(16) NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(user_id, fcm_token)
);

CREATE TABLE IF NOT EXISTS otp_challenges (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    phone VARCHAR(24) NOT NULL,
    requester_ip VARCHAR(45),
    otp_code_hash TEXT NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    attempt_count SMALLINT NOT NULL DEFAULT 0,
    is_verified BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 4. DEVICE MODELS (with status_response_regex) & INVENTORY
CREATE TABLE IF NOT EXISTS device_models (
    id VARCHAR(32) PRIMARY KEY,
    protocol VARCHAR(24) NOT NULL,
    cut_cmd_template VARCHAR(64) NOT NULL,
    restore_cmd_template VARCHAR(64) NOT NULL,
    status_cmd_template VARCHAR(64) NOT NULL,
    status_response_regex VARCHAR(120) NOT NULL DEFAULT 'ACC[:=]\s*(OFF|0).*SPEED[:=]\s*0',
    timer_active_cmd_template VARCHAR(64) NOT NULL,
    timer_normal_cmd_template VARCHAR(64) NOT NULL,
    heartbeat_armed_cmd_template VARCHAR(64) NOT NULL,
    heartbeat_normal_cmd_template VARCHAR(64) NOT NULL,
    supports_ac_input BOOLEAN NOT NULL DEFAULT FALSE,
    supports_accelerometer_events BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS inventory_devices (
    imei VARCHAR(15) PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    model_id VARCHAR(32) NOT NULL REFERENCES device_models(id),
    barcode_serial VARCHAR(40) UNIQUE NOT NULL,
    sim_msisdn VARCHAR(24),
    sim_iccid VARCHAR(24),
    apn_name VARCHAR(64) NOT NULL DEFAULT 'gpinternet',
    device_password_encrypted BYTEA NOT NULL,
    current_holder_dealer_id UUID REFERENCES users(id),
    provisioning_state VARCHAR(24) NOT NULL DEFAULT 'InStock',
    current_heartbeat_interval_sec SMALLINT NOT NULL DEFAULT 180,
    last_connected_at_utc TIMESTAMPTZ,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inventory_movements (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    imei VARCHAR(15) NOT NULL REFERENCES inventory_devices(imei),
    from_user_id UUID REFERENCES users(id),
    to_user_id UUID NOT NULL REFERENCES users(id),
    movement_type VARCHAR(24) NOT NULL,
    moved_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 5. ASSETS & PROFILES (with auto_arm_due_at_utc, safety_state & watch_contacts)
CREATE TABLE IF NOT EXISTS assets (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    branch_id UUID REFERENCES branches(id),
    origin_dealer_id UUID REFERENCES users(id),
    current_imei VARCHAR(15) UNIQUE REFERENCES inventory_devices(imei),
    category VARCHAR(16) NOT NULL,
    name VARCHAR(100) NOT NULL,
    identifier_label VARCHAR(40),
    state_version BIGINT NOT NULL DEFAULT 1,
    service_mode_until_utc TIMESTAMPTZ,
    last_telemetry_timestamp_utc TIMESTAMPTZ,
    last_heartbeat_utc TIMESTAMPTZ,
    subscription_expires_at_utc TIMESTAMPTZ NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS asset_members (
    asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    member_role VARCHAR(24) NOT NULL,
    call_priority_order SMALLINT NOT NULL DEFAULT 1,
    can_control_engine BOOLEAN NOT NULL DEFAULT FALSE,
    can_listen_audio BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (asset_id, user_id)
);

CREATE TABLE IF NOT EXISTS vehicle_profiles (
    asset_id UUID PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    armed_state VARCHAR(24) NOT NULL DEFAULT 'Disarmed',
    auto_arm_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    auto_arm_delay_seconds SMALLINT NOT NULL DEFAULT 600,
    auto_arm_due_at_utc TIMESTAMPTZ,
    grace_expires_at_utc TIMESTAMPTZ,
    vibration_count_window SMALLINT NOT NULL DEFAULT 0,
    last_vibration_utc TIMESTAMPTZ,
    relay_state VARCHAR(24) NOT NULL DEFAULT 'Unlocked',
    parked_location GEOGRAPHY(Point, 4326),
    parked_location_quality VARCHAR(16) NOT NULL DEFAULT 'GpsValid',
    estimated_km_per_liter REAL,
    odometer_meters BIGINT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS driver_schedules (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    driver_user_id UUID REFERENCES users(id),
    active_days SMALLINT[] NOT NULL,
    duty_start_local TIME NOT NULL,
    duty_end_local TIME NOT NULL
);

CREATE TABLE IF NOT EXISTS person_pet_profiles (
    asset_id UUID PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    safety_state VARCHAR(32) NOT NULL DEFAULT 'Normal',
    state_changed_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    qr_public_token VARCHAR(32) UNIQUE,
    home_wifi_bssids TEXT[] NOT NULL DEFAULT '{}',
    missing_wifi_scan_streak SMALLINT NOT NULL DEFAULT 0,
    is_lost_mode_active BOOLEAN NOT NULL DEFAULT FALSE,
    audio_listen_consent_given BOOLEAN NOT NULL DEFAULT FALSE,
    audio_consent_timestamp_utc TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS watch_contacts (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    contact_name VARCHAR(64) NOT NULL,
    phone_number VARCHAR(24) NOT NULL,
    slot_index SMALLINT NOT NULL,
    is_sos_number BOOLEAN NOT NULL DEFAULT FALSE,
    synced_at_utc TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS asset_device_swaps (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    asset_id UUID NOT NULL REFERENCES assets(id),
    old_imei VARCHAR(15),
    new_imei VARCHAR(15),
    swapped_by_user_id UUID NOT NULL REFERENCES users(id),
    reason VARCHAR(120),
    swapped_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 6. GEOFENCES, WIFI FINGERPRINTS & SCHEDULES
CREATE TABLE IF NOT EXISTS geofences (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    branch_id UUID REFERENCES branches(id),
    name VARCHAR(100) NOT NULL,
    zone_type VARCHAR(24) NOT NULL,
    center_point GEOGRAPHY(Point, 4326),
    safe_radius_m SMALLINT,
    danger_radius_m SMALLINT,
    hysteresis_buffer_m SMALLINT NOT NULL DEFAULT 30,
    polygon_boundary GEOGRAPHY(Polygon, 4326),
    CHECK ((center_point IS NOT NULL AND safe_radius_m IS NOT NULL) OR polygon_boundary IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS geofence_wifi_fingerprints (
    geofence_id UUID NOT NULL REFERENCES geofences(id) ON DELETE CASCADE,
    bssid VARCHAR(17) NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 1,
    last_seen_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (geofence_id, bssid)
);

CREATE TABLE IF NOT EXISTS child_schedules (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    asset_id UUID NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    geofence_id UUID NOT NULL REFERENCES geofences(id) ON DELETE CASCADE,
    active_days SMALLINT[] NOT NULL,
    arrive_by_local TIME NOT NULL,
    leave_from_local TIME NOT NULL,
    skip_today_override_date DATE
);

CREATE TABLE IF NOT EXISTS geofence_holidays (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    geofence_id UUID NOT NULL REFERENCES geofences(id) ON DELETE CASCADE,
    holiday_date DATE NOT NULL,
    description VARCHAR(100),
    UNIQUE(geofence_id, holiday_date)
);

-- 7. COMMANDS & IMMUTABLE SECURITY AUDIT LOGS
CREATE TABLE IF NOT EXISTS device_commands (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    asset_id UUID NOT NULL REFERENCES assets(id),
    imei VARCHAR(15) NOT NULL,
    command_type VARCHAR(32) NOT NULL,
    delivery_channel VARCHAR(12) NOT NULL DEFAULT 'TCP',
    status VARCHAR(24) NOT NULL,
    superseded_by_command_id UUID REFERENCES device_commands(id),
    issued_by_user_id UUID REFERENCES users(id),
    auth_method VARCHAR(24) NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at_utc TIMESTAMPTZ,
    ack_at_utc TIMESTAMPTZ,
    raw_response TEXT
);

CREATE TABLE IF NOT EXISTS security_audit_logs (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    asset_id UUID,
    actor_user_id UUID,
    action_type VARCHAR(48) NOT NULL,
    source_ip_or_cli VARCHAR(64),
    details_jsonb JSONB NOT NULL DEFAULT '{}',
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 8. HYPERTABLES WITH PK (asset_id, device_timestamp_utc, packet_type)
CREATE TABLE IF NOT EXISTS telemetry_history (
    asset_id UUID NOT NULL,
    device_timestamp_utc TIMESTAMPTZ NOT NULL,
    packet_type SMALLINT NOT NULL,
    tenant_id UUID NOT NULL,
    server_received_utc TIMESTAMPTZ NOT NULL,
    imei VARCHAR(15) NOT NULL,
    location GEOGRAPHY(Point, 4326),
    location_source VARCHAR(12) NOT NULL DEFAULT 'GPS',
    accuracy_m SMALLINT NOT NULL DEFAULT 10,
    speed_kmh REAL NOT NULL DEFAULT 0,
    course SMALLINT NOT NULL DEFAULT 0,
    satellites SMALLINT NOT NULL DEFAULT 0,
    is_gps_valid BOOLEAN NOT NULL DEFAULT FALSE,
    ignition BOOLEAN,
    main_power_connected BOOLEAN,
    battery_percent SMALLINT,
    external_voltage REAL,
    gsm_signal SMALLINT,
    alarm_code VARCHAR(32),
    is_historical_replay BOOLEAN NOT NULL DEFAULT FALSE,
    is_clock_skewed BOOLEAN NOT NULL DEFAULT FALSE,
    attributes_jsonb JSONB,
    PRIMARY KEY (asset_id, device_timestamp_utc, packet_type)
);

SELECT create_hypertable('telemetry_history', 'device_timestamp_utc', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);
ALTER TABLE telemetry_history SET (timescaledb.compress, timescaledb.compress_segmentby = 'asset_id', timescaledb.compress_orderby = 'device_timestamp_utc DESC');
SELECT add_compression_policy('telemetry_history', INTERVAL '7 days', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry_history', INTERVAL '365 days', if_not_exists => TRUE);

CREATE TABLE IF NOT EXISTS telemetry_person (LIKE telemetry_history INCLUDING ALL);
SELECT create_hypertable('telemetry_person', 'device_timestamp_utc', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);
ALTER TABLE telemetry_person SET (timescaledb.compress, timescaledb.compress_segmentby = 'asset_id', timescaledb.compress_orderby = 'device_timestamp_utc DESC');
SELECT add_compression_policy('telemetry_person', INTERVAL '7 days', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry_person', INTERVAL '90 days', if_not_exists => TRUE);

-- 9. LEDGER WITH DEFERRABLE BALANCE TRIGGER & GIST OVERLAP EXCLUSION
CREATE TABLE IF NOT EXISTS commission_rates (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    dealer_user_id UUID NOT NULL REFERENCES users(id),
    asset_category VARCHAR(16) NOT NULL,
    commission_percent NUMERIC(5,2) NOT NULL CHECK (commission_percent >= 0 AND commission_percent <= 15.00),
    valid_from_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    valid_to_utc TIMESTAMPTZ,
    EXCLUDE USING gist (
        dealer_user_id WITH =,
        asset_category WITH =,
        tstzrange(valid_from_utc, COALESCE(valid_to_utc, 'infinity'::timestamptz)) WITH &&
    )
);

CREATE TABLE IF NOT EXISTS ledger_accounts (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    owner_user_id UUID REFERENCES users(id),
    account_type VARCHAR(32) NOT NULL,
    currency VARCHAR(3) NOT NULL DEFAULT 'BDT'
);

CREATE TABLE IF NOT EXISTS ledger_transactions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    idempotency_key VARCHAR(64) UNIQUE NOT NULL,
    bkash_trx_id VARCHAR(64) UNIQUE,
    reference_asset_id UUID REFERENCES assets(id),
    description VARCHAR(200),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS ledger_entries (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    transaction_id UUID NOT NULL REFERENCES ledger_transactions(id),
    account_id UUID NOT NULL REFERENCES ledger_accounts(id),
    debit NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK (debit >= 0),
    credit NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK (credit >= 0),
    commission_rate_snapshot NUMERIC(5,2),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))
);

CREATE OR REPLACE FUNCTION check_ledger_transaction_balanced() RETURNS TRIGGER AS $$
DECLARE
    v_diff NUMERIC(12,2);
BEGIN
    SELECT COALESCE(SUM(debit), 0) - COALESCE(SUM(credit), 0)
    INTO v_diff
    FROM ledger_entries
    WHERE transaction_id = COALESCE(NEW.transaction_id, OLD.transaction_id);

    IF v_diff <> 0 THEN
        RAISE EXCEPTION 'Unbalanced ledger transaction %: net difference is %', COALESCE(NEW.transaction_id, OLD.transaction_id), v_diff;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_enforce_balanced_ledger ON ledger_entries;
CREATE CONSTRAINT TRIGGER trg_enforce_balanced_ledger
AFTER INSERT OR UPDATE OR DELETE ON ledger_entries
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION check_ledger_transaction_balanced();

-- 10. FAIL-CLOSED RLS POLICY ACROSS ALL 26 TENANT OLTP TABLES (NULLIF Guard)
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_policy ON tenants;
CREATE POLICY tenant_isolation_policy ON tenants
    USING (hierarchy_path <@ NULLIF(current_setting('app.current_tenant_path', true), '')::ltree)
    WITH CHECK (hierarchy_path <@ NULLIF(current_setting('app.current_tenant_path', true), '')::ltree);

DO $$
DECLARE
    tbl TEXT;
    oltp_tables TEXT[] := ARRAY[
        'tenant_holidays', 'tenant_provider_configs', 'organizations', 'branches',
        'users', 'user_push_tokens', 'otp_challenges', 'inventory_devices',
        'inventory_movements', 'assets', 'asset_members', 'vehicle_profiles',
        'driver_schedules', 'person_pet_profiles', 'watch_contacts', 'asset_device_swaps',
        'geofences', 'child_schedules', 'geofence_holidays', 'device_commands',
        'security_audit_logs', 'commission_rates', 'ledger_accounts',
        'ledger_transactions', 'ledger_entries'
    ];
BEGIN
    FOREACH tbl IN ARRAY oltp_tables
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY;', tbl);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY;', tbl);
        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation_policy ON %I;', tbl);
        EXECUTE format(
            'CREATE POLICY tenant_isolation_policy ON %I ' ||
            'USING (tenant_id IN (SELECT id FROM tenants WHERE hierarchy_path <@ NULLIF(current_setting(''app.current_tenant_path'', true), '''')::ltree)) ' ||
            'WITH CHECK (tenant_id IN (SELECT id FROM tenants WHERE hierarchy_path <@ NULLIF(current_setting(''app.current_tenant_path'', true), '''')::ltree));',
            tbl
        );
    END LOOP;
END
$$;

-- 11. GRANT TABLE PERMISSIONS & ENFORCE AUDIT LOG IMMUTABILITY
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO yoapp_user, yoworker_service;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO yoapp_user, yoworker_service;
REVOKE UPDATE, DELETE, TRUNCATE ON security_audit_logs FROM yoapp_user, yoworker_service;
