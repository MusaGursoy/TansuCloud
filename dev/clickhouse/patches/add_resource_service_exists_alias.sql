-- Add SigNoz Explorer-required alias that indicates existence of service.name in resources_string
-- Applies to both the base and distributed index tables used by queries

ALTER TABLE signoz_traces.signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource_string_service$$name_exists` Bool
    ALIAS toBool(mapContains(resources_string, 'service.name'));

ALTER TABLE signoz_traces.distributed_signoz_index_v3 ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `resource_string_service$$name_exists` Bool
    ALIAS toBool(mapContains(resources_string, 'service.name'));

-- Verification: list the resource_string_service$$name* columns on both tables
SELECT database, table, name, type, default_type, default_expression
FROM system.columns
WHERE database = 'signoz_traces'
  AND table IN ('signoz_index_v3','distributed_signoz_index_v3')
  AND name LIKE 'resource_string_service%';
