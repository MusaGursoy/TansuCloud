-- Idempotent patch: ensure 'resource' column exists as a real column across base and distributed index tables
-- We keep semantics identical to alias by defaulting to resources_string; collector expects a writable column named 'resource'

-- Base table: drop alias (if present) then add real column
ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    DROP COLUMN IF EXISTS resource;
ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS resource Map(LowCardinality(String), String) DEFAULT resources_string;

-- Distributed table: drop alias (if present) then add real column (mirrors base schema for inserts)
ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    DROP COLUMN IF EXISTS resource;
ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS resource Map(LowCardinality(String), String) DEFAULT resources_string;
