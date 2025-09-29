-- Create missing table for span attribute keys (local + distributed)
-- Based on SigNoz traces_migrations.go (MigrationID ~1008 additions)

CREATE TABLE IF NOT EXISTS signoz_traces.span_attributes_keys ON CLUSTER cluster
(
    `tagKey` String,
    `tagType` LowCardinality(String),         -- 'tag' | 'resource'
    `dataType` LowCardinality(String),        -- 'string' | 'bool' | 'float64'
    `isColumn` Bool,
    `timestamp` DateTime DEFAULT now()        -- added in later migration
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
