-- acquired_at: since when the current owner name holds the lock (server time); NULL = unknown.
-- The ALTER runs only while the column is missing: even when IF NOT EXISTS makes it a no-op, ALTER TABLE
-- takes an ACCESS EXCLUSIVE lock, which waits behind any open reader of the table and makes every later
-- acquire wait behind it, and this file runs on every start.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = 'cluster_distributed_lock'::regclass
          AND attname = 'acquired_at'
          AND NOT attisdropped)
    THEN
        ALTER TABLE cluster_distributed_lock ADD COLUMN IF NOT EXISTS acquired_at TIMESTAMPTZ;
    END IF;
END
$$;
