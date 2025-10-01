-- Dev-only usage tables to silence Signoz collector warnings
-- These tables are referenced by usage collectors in some builds; harmless if not used.

CREATE DATABASE IF NOT EXISTS signoz_traces;

-- Local usage table
CREATE TABLE IF NOT EXISTS signoz_traces.usage_local
(
    `ts` DateTime DEFAULT now(),
    `component` LowCardinality(String),
    `name` String,
    `value` Float64,
    `labels` Map(String, String)
)
ENGINE = MergeTree
ORDER BY (component, name, ts)
TTL ts + INTERVAL 30 DAY;

-- Distributed wrapper for usage
CREATE TABLE IF NOT EXISTS signoz_traces.distributed_usage AS signoz_traces.usage_local
ENGINE = Distributed('cluster', 'signoz_traces', 'usage_local', rand());
