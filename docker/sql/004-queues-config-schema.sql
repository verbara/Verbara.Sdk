-- 004-queues-config-schema.sql
-- Sprint 6: Queue Advanced Config tables (Dashboard-managed, per-server partitioned)

CREATE TABLE IF NOT EXISTS queues_config (
    id                           SERIAL PRIMARY KEY,
    server_id                    VARCHAR(40)  NOT NULL,
    name                         VARCHAR(128) NOT NULL,
    strategy                     VARCHAR(20)  NOT NULL DEFAULT 'ringall',
    timeout                      INT          NOT NULL DEFAULT 15,
    retry                        INT          NOT NULL DEFAULT 5,
    maxlen                       INT          NOT NULL DEFAULT 0,
    wrapuptime                   INT          NOT NULL DEFAULT 0,
    servicelevel                 INT          NOT NULL DEFAULT 60,
    musiconhold                  VARCHAR(100) NOT NULL DEFAULT 'default',
    weight                       INT          NOT NULL DEFAULT 0,
    joinempty                    VARCHAR(20)  NOT NULL DEFAULT 'yes',
    leavewhenempty               VARCHAR(20)  NOT NULL DEFAULT 'no',
    ringinuse                    VARCHAR(3)   NOT NULL DEFAULT 'no',
    announce_frequency           INT          NOT NULL DEFAULT 0,
    announce_holdtime            VARCHAR(10)  NOT NULL DEFAULT 'no',
    announce_position            VARCHAR(10)  NOT NULL DEFAULT 'no',
    periodic_announce            VARCHAR(255),
    periodic_announce_frequency  INT          NOT NULL DEFAULT 0,
    queue_youarenext             VARCHAR(255),
    queue_thereare               VARCHAR(255),
    queue_callswaiting           VARCHAR(255),
    enabled                      BOOLEAN      NOT NULL DEFAULT true,
    notes                        TEXT,
    UNIQUE(server_id, name)
);

CREATE INDEX IF NOT EXISTS idx_queues_config_server ON queues_config(server_id);

CREATE TABLE IF NOT EXISTS queue_members_config (
    id                SERIAL PRIMARY KEY,
    queue_config_id   INT          NOT NULL REFERENCES queues_config(id) ON DELETE CASCADE,
    interface         VARCHAR(128) NOT NULL,
    membername        VARCHAR(128),
    state_interface   VARCHAR(128),
    penalty           INT          NOT NULL DEFAULT 0,
    paused            INT          NOT NULL DEFAULT 0,
    UNIQUE(queue_config_id, interface)
);

CREATE INDEX IF NOT EXISTS idx_queue_members_config_queue ON queue_members_config(queue_config_id);
