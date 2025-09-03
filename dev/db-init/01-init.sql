-- Enable Citus and useful extensions (idempotent) and create dev databases on first init
-- This file is mounted to /docker-entrypoint-initdb.d in the Citus container

CREATE EXTENSION IF NOT EXISTS citus;
-- Vector similarity (requires pgvector package installed in the image)
CREATE EXTENSION IF NOT EXISTS vector;
-- Trigram search (handy for text search)
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- App databases (executed only on first container init)
-- Identity DB (used by TansuCloud.Identity when PostgreSQL is enabled)
CREATE DATABASE tansu_identity;

-- Optionally, install extensions into template1 so new databases inherit them by default
\connect template1
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
\connect postgres

-- Ensure extensions exist in the identity DB as well
\connect tansu_identity
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
\connect postgres
