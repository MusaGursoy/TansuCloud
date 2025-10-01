-- Replace alias 'resource' with a real JSON column on v3 index tables (local + distributed)
-- This aligns with collector inserts that target 'resource' in use_new_schema mode.

-- Drop alias if it exists and is an alias, then add real column.
-- Note: ClickHouse doesn't support conditional drop-if-alias directly; use try-catch via multi-step queries is not available.
-- We can attempt to drop the column if it exists; if it was a real column already, this is idempotent due to IF EXISTS.

ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    DROP COLUMN IF EXISTS resource;

ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    DROP COLUMN IF EXISTS resource;

-- Add real JSON column for resource on both tables
ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));

ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));

-- Optional backfill: when resources_string exists, set resource to resources_string JSON
-- Skipped for now; collector will populate going forward. Existing rows can remain empty.

-- Verify
SELECT database, table, name, type, default_type
FROM system.columns
WHERE database = 'signoz_traces'
  AND table IN ('signoz_index_v3','distributed_signoz_index_v3')
  AND name = 'resource';
