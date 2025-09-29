-- Ensure the JSON `resource` column exists on v3 trace index tables (local + distributed)
-- Matches SigNoz migration (TracesMigrations: add JSON resource column)

ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));

ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));

-- Verification: ensure both tables have the `resource` column
SELECT database, table, name, type
FROM system.columns
WHERE database = 'signoz_traces'
  AND table IN ('signoz_index_v3','distributed_signoz_index_v3')
  AND name = 'resource';
