-- 005-ivr-menus-schema.sql
-- IVR menu configuration tables for Sprint 7

CREATE TABLE IF NOT EXISTS ivr_menus (
    id                  SERIAL PRIMARY KEY,
    server_id           TEXT NOT NULL,
    name                TEXT NOT NULL,
    label               TEXT NOT NULL,
    greeting            TEXT,
    timeout             INT NOT NULL DEFAULT 5,
    max_retries         INT NOT NULL DEFAULT 3,
    invalid_dest_type   TEXT,
    invalid_dest        TEXT,
    timeout_dest_type   TEXT,
    timeout_dest        TEXT,
    enabled             BOOL NOT NULL DEFAULT TRUE,
    notes               TEXT,
    UNIQUE(server_id, name)
);

CREATE TABLE IF NOT EXISTS ivr_menu_items (
    id              SERIAL PRIMARY KEY,
    menu_id         INT NOT NULL REFERENCES ivr_menus(id) ON DELETE CASCADE,
    digit           TEXT NOT NULL,
    label           TEXT,
    dest_type       TEXT NOT NULL,
    dest_target     TEXT NOT NULL,
    trunk           TEXT,
    UNIQUE(menu_id, digit)
);

CREATE INDEX idx_ivr_menus_server ON ivr_menus(server_id);
CREATE INDEX idx_ivr_menu_items_menu ON ivr_menu_items(menu_id);
CREATE INDEX idx_ivr_menu_items_dest ON ivr_menu_items(dest_type, dest_target);
