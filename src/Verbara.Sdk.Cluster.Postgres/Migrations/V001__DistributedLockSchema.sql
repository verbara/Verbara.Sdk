CREATE TABLE IF NOT EXISTS cluster_distributed_lock (
    resource TEXT PRIMARY KEY,
    owner    TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE cluster_distributed_lock IS 'Cluster-wide distributed lock with TTL. See Verbara.Sdk.Cluster.Postgres.PostgresDistributedLock and ADR-0022 Phase A.5.';
