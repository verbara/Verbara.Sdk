-- Class of Service (COS) schema
-- Manages dial restrictions per extension/group

CREATE TABLE cos_pattern_groups (
    id              SERIAL PRIMARY KEY,
    server_id       VARCHAR(40) NOT NULL,
    name            VARCHAR(100) NOT NULL,
    description     VARCHAR(500),
    patterns        TEXT[] NOT NULL,
    country_code    VARCHAR(10),
    is_built_in     BOOLEAN DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(server_id, name)
);

CREATE TABLE cos_levels (
    id              SERIAL PRIMARY KEY,
    server_id       VARCHAR(40) NOT NULL,
    name            VARCHAR(50) NOT NULL,
    description     VARCHAR(500),
    priority        INT NOT NULL,
    asterisk_context VARCHAR(50) NOT NULL,
    is_built_in     BOOLEAN DEFAULT FALSE,
    allow_premium           BOOLEAN DEFAULT FALSE,
    allow_mobile            BOOLEAN DEFAULT TRUE,
    allow_forward_external  BOOLEAN DEFAULT FALSE,
    allow_conference_external BOOLEAN DEFAULT FALSE,
    allow_recording_control BOOLEAN DEFAULT FALSE,
    enabled         BOOLEAN DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(server_id, name)
);

CREATE TABLE cos_level_rules (
    id              SERIAL PRIMARY KEY,
    cos_level_id    INT NOT NULL REFERENCES cos_levels(id) ON DELETE CASCADE,
    pattern_group_id INT NOT NULL REFERENCES cos_pattern_groups(id),
    action          VARCHAR(5) NOT NULL CHECK (action IN ('ALLOW','DENY')),
    sequence        INT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(cos_level_id, sequence)
);

CREATE TABLE cos_extension_overrides (
    id              SERIAL PRIMARY KEY,
    server_id       VARCHAR(40) NOT NULL,
    extension       VARCHAR(20) NOT NULL,
    cos_level_id    INT REFERENCES cos_levels(id),
    pattern_overrides JSONB,
    notes           VARCHAR(500),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(server_id, extension)
);

CREATE TABLE cos_time_windows (
    id              SERIAL PRIMARY KEY,
    name            VARCHAR(100) NOT NULL,
    server_id       VARCHAR(40) NOT NULL,
    day_of_week     INT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time      TIME NOT NULL,
    end_time        TIME NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(server_id, name, day_of_week)
);

CREATE TABLE cos_time_overrides (
    id                      SERIAL PRIMARY KEY,
    cos_level_id            INT NOT NULL REFERENCES cos_levels(id) ON DELETE CASCADE,
    override_cos_level_id   INT NOT NULL REFERENCES cos_levels(id),
    time_window_id          INT NOT NULL REFERENCES cos_time_windows(id),
    priority                INT NOT NULL DEFAULT 100,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE cos_audit_log (
    id              SERIAL PRIMARY KEY,
    server_id       VARCHAR(40) NOT NULL,
    entity_type     VARCHAR(20) NOT NULL,
    entity_id       VARCHAR(50) NOT NULL,
    action          VARCHAR(20) NOT NULL,
    old_value       VARCHAR(255),
    new_value       VARCHAR(255),
    changed_by      VARCHAR(100),
    changed_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_cos_levels_server ON cos_levels(server_id);
CREATE INDEX idx_cos_pattern_groups_server ON cos_pattern_groups(server_id);
CREATE INDEX idx_cos_ext_overrides_server ON cos_extension_overrides(server_id);
CREATE INDEX idx_cos_audit_server ON cos_audit_log(server_id, changed_at DESC);
CREATE INDEX idx_cos_time_windows_server ON cos_time_windows(server_id);
