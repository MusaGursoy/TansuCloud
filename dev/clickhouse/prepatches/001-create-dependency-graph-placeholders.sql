-- Create minimal placeholder objects for dependency graph v2 so migrator 1002 can ALTER them.
-- These definitions are intentionally minimal and idempotent. Migrator 1002 will MODIFY QUERY on the MV.

-- 1) Target table for minutes DB calls aggregated graph
CREATE TABLE IF NOT EXISTS signoz_traces.dependency_graph_minutes_db_calls_v2 ON CLUSTER cluster
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

CREATE TABLE IF NOT EXISTS signoz_traces.distributed_dependency_graph_minutes_db_calls_v2 ON CLUSTER cluster
AS signoz_traces.dependency_graph_minutes_db_calls_v2
ENGINE = Distributed('cluster', 'signoz_traces', 'dependency_graph_minutes_db_calls_v2', rand());

-- 2) Materialized view that migrator 1002 tries to ALTER
CREATE MATERIALIZED VIEW IF NOT EXISTS signoz_traces.dependency_graph_minutes_db_calls_mv_v2 ON CLUSTER cluster
TO signoz_traces.dependency_graph_minutes_db_calls_v2
AS SELECT now() AS timestamp, '' AS src, '' AS dest,
          quantilesState(0.5)(toFloat64(0)) AS duration_quantiles_state,
          toUInt64(0) AS error_count,
          toUInt64(0) AS total_count,
          CAST(NULL AS Nullable(String)) as deployment_environment,
          CAST(NULL AS Nullable(String)) as k8s_cluster_name,
          CAST(NULL AS Nullable(String)) as k8s_namespace_name
FROM system.one;