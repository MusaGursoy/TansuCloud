-- Align span_attributes_keys schema to collector version that inserts 5 values (includes `timestamp`).
-- Ensure local table has `timestamp` column with TTL; ensure distributed mirror includes the column as well.

-- Add timestamp to local if missing
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `timestamp` DateTime DEFAULT now();

-- Ensure TTL exists on local table (safe if already present; will set/replace TTL expression)
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster
    MODIFY TTL `timestamp` + toIntervalDay(30);

-- Add timestamp column to distributed table (Distributed engine ignores TTL at table level, so we only add column)
ALTER TABLE signoz_traces.distributed_span_attributes_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `timestamp` DateTime DEFAULT now();
