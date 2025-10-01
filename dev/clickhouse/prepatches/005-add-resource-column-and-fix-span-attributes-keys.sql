-- Ensure resource column exists on signoz_index_v3 (local + distributed) as DEFAULT resources_string
-- and align span_attributes_keys schema to 4 columns (no timestamp), then recreate distributed table accordingly.

-- 1) Resource column compatibility (SigNoz collector expects `resource` key map)
ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS resource Map(LowCardinality(String), String) DEFAULT resources_string AFTER resources_string;

ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS resource Map(LowCardinality(String), String) DEFAULT resources_string AFTER resources_string;

-- 2) span_attributes_keys to 4 columns
-- Remove TTL to allow dropping timestamp if present
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster REMOVE TTL;

-- Drop timestamp if it exists (idempotent)
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster DROP COLUMN IF EXISTS timestamp;

-- Recreate distributed table to mirror local structure exactly
DROP TABLE IF EXISTS signoz_traces.distributed_span_attributes_keys ON CLUSTER cluster;

CREATE TABLE IF NOT EXISTS signoz_traces.distributed_span_attributes_keys ON CLUSTER cluster
AS signoz_traces.span_attributes_keys
ENGINE = Distributed('cluster', 'signoz_traces', 'span_attributes_keys', rand());
