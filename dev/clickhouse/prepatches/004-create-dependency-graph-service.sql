-- Creates minimal required objects for service->service dependency graph used by migration 1002

CREATE TABLE IF NOT EXISTS signoz_traces.dependency_graph_minutes_service_calls_v2
(
    `timestamp` DateTime,
    `src` String,
    `dest` String,
    `duration_quantiles_state` AggregateFunction(quantilesState(0.5, 0.75, 0.9, 0.95, 0.99), Float64),
    `error_count` UInt64,
    `total_count` UInt64,
    `deployment_environment` Nullable(String),
    `k8s_cluster_name` Nullable(String),
    `k8s_namespace_name` Nullable(String)
)
ENGINE = AggregatingMergeTree
ORDER BY (timestamp, src, dest);

CREATE TABLE IF NOT EXISTS signoz_traces.distributed_dependency_graph_minutes_service_calls_v2 AS signoz_traces.dependency_graph_minutes_service_calls_v2
ENGINE = Distributed('cluster', 'signoz_traces', 'dependency_graph_minutes_service_calls_v2', rand());

CREATE MATERIALIZED VIEW IF NOT EXISTS signoz_traces.dependency_graph_minutes_service_calls_mv_v2
TO signoz_traces.dependency_graph_minutes_service_calls_v2
AS
SELECT
    toStartOfMinute(now()) AS timestamp,
    CAST('' AS String) AS src,
    CAST('' AS String) AS dest,
    quantilesState(0.5, 0.75, 0.9, 0.95, 0.99)(toFloat64(0)) AS duration_quantiles_state,
    toUInt64(0) AS error_count,
    toUInt64(0) AS total_count,
    CAST(NULL AS Nullable(String)) AS deployment_environment,
    CAST(NULL AS Nullable(String)) AS k8s_cluster_name,
    CAST(NULL AS Nullable(String)) AS k8s_namespace_name
FROM system.one;
