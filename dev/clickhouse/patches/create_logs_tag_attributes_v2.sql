-- Create missing logs tag attributes tables (local + distributed)
-- Based on SigNoz logs_migrations.go

CREATE TABLE IF NOT EXISTS signoz_logs.tag_attributes_v2 ON CLUSTER cluster
(
    `unix_milli` UInt64,
    `tag_key` String,
    `tag_type` LowCardinality(String),       -- e.g., 'resource' | 'attribute'
    `tag_data_type` LowCardinality(String),  -- e.g., 'string' | 'number'
    `string_value` String,
    `number_value` Nullable(Float64)
)
ENGINE = ReplacingMergeTree
ORDER BY (tag_key, tag_type, tag_data_type, unix_milli)
TTL toDateTime(unix_milli / 1000) + toIntervalDay(30)
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS signoz_logs.distributed_tag_attributes_v2 ON CLUSTER cluster
(
    `unix_milli` UInt64,
    `tag_key` String,
    `tag_type` LowCardinality(String),
    `tag_data_type` LowCardinality(String),
    `string_value` String,
    `number_value` Nullable(Float64)
)
ENGINE = Distributed('cluster', 'signoz_logs', 'tag_attributes_v2', rand());
