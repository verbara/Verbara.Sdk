-- Routes + Time Conditions schema (Sprint 5)

CREATE TABLE routes_inbound (
    id          SERIAL PRIMARY KEY,
    server_id   VARCHAR(40) NOT NULL,
    name        VARCHAR(100) NOT NULL,
    did_pattern VARCHAR(40) NOT NULL,
    destination_type VARCHAR(40) NOT NULL,
    destination      VARCHAR(100) NOT NULL,
    priority    INT NOT NULL DEFAULT 100,
    enabled     BOOLEAN NOT NULL DEFAULT TRUE,
    notes       TEXT,
    UNIQUE(server_id, did_pattern)
);

CREATE TABLE routes_outbound (
    id          SERIAL PRIMARY KEY,
    server_id   VARCHAR(40) NOT NULL,
    name        VARCHAR(100) NOT NULL,
    dial_pattern VARCHAR(40) NOT NULL,
    prepend     VARCHAR(20),
    prefix      VARCHAR(20),
    priority    INT NOT NULL DEFAULT 100,
    enabled     BOOLEAN NOT NULL DEFAULT TRUE,
    notes       TEXT
);

CREATE TABLE route_trunks (
    id              SERIAL PRIMARY KEY,
    outbound_route_id INT NOT NULL REFERENCES routes_outbound(id) ON DELETE CASCADE,
    trunk_name      VARCHAR(100) NOT NULL,
    trunk_technology VARCHAR(10) NOT NULL,
    sequence        INT NOT NULL,
    UNIQUE(outbound_route_id, sequence)
);

CREATE TABLE time_conditions (
    id              SERIAL PRIMARY KEY,
    server_id       VARCHAR(40) NOT NULL,
    name            VARCHAR(100) NOT NULL,
    match_dest_type VARCHAR(40) NOT NULL,
    match_dest      VARCHAR(100) NOT NULL,
    nomatch_dest_type VARCHAR(40) NOT NULL,
    nomatch_dest    VARCHAR(100) NOT NULL,
    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE(server_id, name)
);

CREATE TABLE time_ranges (
    id                  SERIAL PRIMARY KEY,
    time_condition_id   INT NOT NULL REFERENCES time_conditions(id) ON DELETE CASCADE,
    day_of_week         INT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time          TIME NOT NULL,
    end_time            TIME NOT NULL
);

CREATE TABLE holidays (
    id                  SERIAL PRIMARY KEY,
    time_condition_id   INT NOT NULL REFERENCES time_conditions(id) ON DELETE CASCADE,
    name                VARCHAR(100) NOT NULL,
    month               INT NOT NULL CHECK (month BETWEEN 1 AND 12),
    day                 INT NOT NULL CHECK (day BETWEEN 1 AND 31),
    recurring           BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS extensions (
    id          SERIAL PRIMARY KEY,
    context     VARCHAR(40) NOT NULL,
    exten       VARCHAR(40) NOT NULL,
    priority    INT NOT NULL,
    app         VARCHAR(40) NOT NULL,
    appdata     VARCHAR(256),
    UNIQUE(context, exten, priority)
);

CREATE INDEX idx_routes_inbound_server ON routes_inbound(server_id);
CREATE INDEX idx_routes_outbound_server ON routes_outbound(server_id);
CREATE INDEX idx_time_conditions_server ON time_conditions(server_id);
CREATE INDEX idx_route_trunks_route ON route_trunks(outbound_route_id);
CREATE INDEX idx_time_ranges_tc ON time_ranges(time_condition_id);
CREATE INDEX idx_holidays_tc ON holidays(time_condition_id);
