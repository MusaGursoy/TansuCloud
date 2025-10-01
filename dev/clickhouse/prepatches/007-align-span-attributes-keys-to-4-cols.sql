-- Align to 5-column schema (collector expects 5 args for tagKey prepared statement):
-- Columns: tagKey, tagType, dataType, isColumn, timestamp; TTL on local by timestamp; distributed mirrors columns.

-- Ensure timestamp exists on local
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `timestamp` DateTime DEFAULT now();

-- Ensure TTL exists on local
ALTER TABLE signoz_traces.span_attributes_keys ON CLUSTER cluster
    MODIFY TTL `timestamp` + toIntervalDay(30);

-- Ensure timestamp exists on distributed (TTL not applicable on Distributed engine)
ALTER TABLE signoz_traces.distributed_span_attributes_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS `timestamp` DateTime DEFAULT now();
