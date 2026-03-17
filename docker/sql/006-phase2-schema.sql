-- Phase 2: Recording, MOH, Conference, Feature Code, and Parking Lot tables
-- These match the schema auto-created by RecordingMohSchemaManager.cs

CREATE TABLE IF NOT EXISTS recording_policies (
    id              SERIAL PRIMARY KEY,
    server_id       TEXT NOT NULL,
    name            TEXT NOT NULL,
    mode            TEXT NOT NULL DEFAULT 'Always',
    format          TEXT NOT NULL DEFAULT 'wav',
    storage_path    TEXT NOT NULL DEFAULT '/var/spool/asterisk/monitor/',
    retention_days  INT NOT NULL DEFAULT 0,
    mix_monitor_options TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (server_id, name)
);

CREATE TABLE IF NOT EXISTS recording_policy_targets (
    id          SERIAL PRIMARY KEY,
    policy_id   INT NOT NULL REFERENCES recording_policies(id) ON DELETE CASCADE,
    target_type TEXT NOT NULL,
    target_value TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_recording_targets_policy ON recording_policy_targets(policy_id);
CREATE INDEX IF NOT EXISTS idx_recording_policies_server ON recording_policies(server_id);

CREATE TABLE IF NOT EXISTS moh_classes (
    id                  SERIAL PRIMARY KEY,
    server_id           TEXT NOT NULL,
    name                TEXT NOT NULL,
    mode                TEXT NOT NULL DEFAULT 'files',
    directory           TEXT NOT NULL,
    sort                TEXT NOT NULL DEFAULT 'random',
    custom_application  TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (server_id, name)
);

CREATE INDEX IF NOT EXISTS idx_moh_classes_server ON moh_classes(server_id);

CREATE TABLE IF NOT EXISTS conference_configs (
    id              SERIAL PRIMARY KEY,
    server_id       TEXT NOT NULL,
    name            TEXT NOT NULL,
    number          TEXT NOT NULL DEFAULT '',
    max_members     INT NOT NULL DEFAULT 0,
    pin             TEXT,
    admin_pin       TEXT,
    record          BOOLEAN NOT NULL DEFAULT false,
    music_on_hold   TEXT NOT NULL DEFAULT 'default',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (server_id, name)
);

CREATE INDEX IF NOT EXISTS idx_conference_configs_server ON conference_configs(server_id);

CREATE TABLE IF NOT EXISTS feature_codes (
    id              SERIAL PRIMARY KEY,
    server_id       TEXT NOT NULL,
    code            TEXT NOT NULL,
    name            TEXT NOT NULL,
    description     TEXT NOT NULL DEFAULT '',
    enabled         BOOLEAN NOT NULL DEFAULT true,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (server_id, code)
);

CREATE INDEX IF NOT EXISTS idx_feature_codes_server ON feature_codes(server_id);

CREATE TABLE IF NOT EXISTS parking_lot_configs (
    id                  SERIAL PRIMARY KEY,
    server_id           TEXT NOT NULL,
    name                TEXT NOT NULL,
    parking_start_slot  INT NOT NULL DEFAULT 701,
    parking_end_slot    INT NOT NULL DEFAULT 720,
    parking_timeout     INT NOT NULL DEFAULT 45,
    music_on_hold       TEXT NOT NULL DEFAULT 'default',
    context             TEXT NOT NULL DEFAULT 'parkedcalls',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (server_id, name)
);

CREATE INDEX IF NOT EXISTS idx_parking_lot_configs_server ON parking_lot_configs(server_id);
