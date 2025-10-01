-- Ensure resource JSON column exists on trace index tables
ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));
ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource` JSON CODEC(ZSTD(1));

-- Create span_attributes_keys tables required by collector writes
CREATE TABLE IF NOT EXISTS signoz_traces.span_attributes_keys ON CLUSTER cluster
(
    `tagKey` String,
    `tagType` LowCardinality(String),
    `dataType` LowCardinality(String),
    `isColumn` Bool,
    `timestamp` DateTime DEFAULT now()
)
ENGINE = ReplacingMergeTree
ORDER BY (tagKey, tagType, dataType, isColumn)
TTL timestamp + toIntervalDay(30);

CREATE TABLE IF NOT EXISTS signoz_traces.distributed_span_attributes_keys ON CLUSTER cluster
(
    `tagKey` String,
    `tagType` LowCardinality(String),
    `dataType` LowCardinality(String),
    `isColumn` Bool,
    `timestamp` DateTime DEFAULT now()
)
ENGINE = Distributed('cluster', 'signoz_traces', 'span_attributes_keys', rand());

-- Create logs tag_attributes_v2 tables required by logs pipeline
CREATE TABLE IF NOT EXISTS signoz_logs.tag_attributes_v2 ON CLUSTER cluster
(
    `unix_milli` UInt64,
    `tag_key` String,
    `tag_type` LowCardinality(String),
    `tag_data_type` LowCardinality(String),
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