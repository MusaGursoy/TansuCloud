-- Enable Citus extension and create dev databases on first init
-- This file is mounted to /docker-entrypoint-initdb.d in the Citus container

-- Extension
CREATE EXTENSION IF NOT EXISTS citus;

-- App databases (executed only on first container init)
-- Identity DB (used by TansuCloud.Identity when PostgreSQL is enabled)
CREATE DATABASE tansu_identity;
