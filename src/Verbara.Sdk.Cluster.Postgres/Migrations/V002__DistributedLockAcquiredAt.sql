ALTER TABLE cluster_distributed_lock ADD COLUMN IF NOT EXISTS acquired_at TIMESTAMPTZ;
