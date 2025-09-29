-- Create missing error index tables required by SigNoz in traces database
-- Based on SigNoz squashed_traces_migrations.go (MigrationID 6 and 7)

CREATE TABLE IF NOT EXISTS signoz_traces.signoz_error_index_v2 ON CLUSTER cluster
(
    `timestamp` DateTime64(9) CODEC(DoubleDelta, LZ4),
    `errorID` FixedString(32) CODEC(ZSTD(1)),
    `groupID` FixedString(32) CODEC(ZSTD(1)),
    `traceID` FixedString(32) CODEC(ZSTD(1)),
    `spanID` String CODEC(ZSTD(1)),
    `serviceName` LowCardinality(String) CODEC(ZSTD(1)),
    `name` LowCardinality(String) CODEC(ZSTD(1)),
    `kind` Int8 CODEC(T64, ZSTD(1)),
    `statusCode` Int16 CODEC(T64, ZSTD(1)),
    `statusMessage` String CODEC(ZSTD(1)),
    `statusCodeString` String CODEC(ZSTD(1)),
    `spanKind` String CODEC(ZSTD(1)),
    `httpRoute` LowCardinality(String) CODEC(ZSTD(1)),
    `externalHttpMethod` LowCardinality(String) CODEC(ZSTD(1)),
    `httpMethod` LowCardinality(String) CODEC(ZSTD(1)),
    `httpHost` LowCardinality(String) CODEC(ZSTD(1)),
    `dbName` LowCardinality(String) CODEC(ZSTD(1)),
    `dbOperation` LowCardinality(String) CODEC(ZSTD(1)),
    `hasError` Bool CODEC(T64, ZSTD(1)),
    `isRemote` LowCardinality(String) CODEC(ZSTD(1)),
    `exceptionType` LowCardinality(String) CODEC(ZSTD(1)),
    `exceptionMessage` String CODEC(ZSTD(1)),
    `exceptionStacktrace` String CODEC(ZSTD(1)),
    `exceptionEscaped` Bool CODEC(T64, ZSTD(1)),
    `resourceTagsMap` Map(LowCardinality(String), String) CODEC(ZSTD(1))
)
ENGINE = MergeTree
PARTITION BY toDate(timestamp)
ORDER BY (timestamp, groupID)
TTL toDateTime(timestamp) + toIntervalSecond(1296000)
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1;

CREATE TABLE IF NOT EXISTS signoz_traces.distributed_signoz_error_index_v2 ON CLUSTER cluster
(
    `timestamp` DateTime64(9) CODEC(DoubleDelta, LZ4),
    `errorID` FixedString(32) CODEC(ZSTD(1)),
    `groupID` FixedString(32) CODEC(ZSTD(1)),
    `traceID` FixedString(32) CODEC(ZSTD(1)),
    `spanID` String CODEC(ZSTD(1)),
    `serviceName` LowCardinality(String) CODEC(ZSTD(1)),
    `name` LowCardinality(String) CODEC(ZSTD(1)),
    `kind` Int8 CODEC(T64, ZSTD(1)),
    `statusCode` Int16 CODEC(T64, ZSTD(1)),
    `statusMessage` String CODEC(ZSTD(1)),
    `statusCodeString` String CODEC(ZSTD(1)),
    `spanKind` String CODEC(ZSTD(1)),
    `httpRoute` LowCardinality(String) CODEC(ZSTD(1)),
    `externalHttpMethod` LowCardinality(String) CODEC(ZSTD(1)),
    `httpMethod` LowCardinality(String) CODEC(ZSTD(1)),
    `httpHost` LowCardinality(String) CODEC(ZSTD(1)),
    `dbName` LowCardinality(String) CODEC(ZSTD(1)),
    `dbOperation` LowCardinality(String) CODEC(ZSTD(1)),
    `hasError` Bool CODEC(T64, ZSTD(1)),
    `isRemote` LowCardinality(String) CODEC(ZSTD(1)),
    `exceptionType` LowCardinality(String) CODEC(ZSTD(1)),
    `exceptionMessage` String CODEC(ZSTD(1)),
    `exceptionStacktrace` String CODEC(ZSTD(1)),
    `exceptionEscaped` Bool CODEC(T64, ZSTD(1)),
    `resourceTagsMap` Map(LowCardinality(String), String) CODEC(ZSTD(1))
)
ENGINE = Distributed('cluster', 'signoz_traces', 'signoz_error_index_v2', cityHash64(groupID));
