-- Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
-- Fix logs_attribute_keys table to include timestamp column if missing
-- This prevents signoz-schema-migrator-async migration 1003 from failing
-- This patch runs AFTER schema migrations create the database

-- Check if logs_attribute_keys table exists and add timestamp if needed
ALTER TABLE IF EXISTS signoz_logs.logs_attribute_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS timestamp DateTime64(9) DEFAULT now64(9);

-- Add same for distributed table if it exists  
ALTER TABLE IF EXISTS signoz_logs.distributed_logs_attribute_keys ON CLUSTER cluster
    ADD COLUMN IF NOT EXISTS timestamp DateTime64(9) DEFAULT now64(9);
