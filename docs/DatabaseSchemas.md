// Tansu.Cloud Public Repository:    <https://github.com/MusaGursoy/TansuCloud>

# TansuCloud Database Schemas

**Version:** 1.0.0  
**Last Updated:** 2025-10-07  
**Purpose:** Authoritative reference for all database schemas, required tables, indexes, extensions, and version tracking across TansuCloud infrastructure.

This document serves as the single source of truth for schema management, startup validation, and operational procedures.

---

## Table of Contents

1. [Overview](#overview)
2. [PostgreSQL Databases](#postgresql-databases)
3. [ClickHouse Databases (SigNoz)](#clickhouse-databases-signoz)
4. [PgCat Pool Configuration](#pgcat-pool-configuration)
5. [Schema Version Tracking](#schema-version-tracking)
6. [Extension Requirements](#extension-requirements)
7. [Startup Validation Requirements](#startup-validation-requirements)
8. [Migration Procedures](#migration-procedures)
9. [Compatibility Matrix](#compatibility-matrix)

---

## Overview

TansuCloud uses multiple database systems for different purposes:

- **PostgreSQL (Citus)**: Primary data storage for identity, tenant documents, collections, vectors, and audit logs
- **ClickHouse (SigNoz)**: Time-series data for distributed traces, metrics, and structured logs
- **PgCat**: Connection pooler for PostgreSQL with per-tenant pool management
- **Redis**: Caching layer for distributed cache and session management

### Current State

- ✅ Extension version checks operational (Task 11 follow-up completed 2025-10-07)
- ✅ Tenant provisioning creates databases and PgCat pools on-demand
- ⚠️ No centralized schema version tracking
- ⚠️ No startup validation of required databases
- ❌ `tansu_audit` database does not exist yet (blocked Task 31 completion)
- ⚠️ PgCat pool reconciliation is manual (no automated drift detection)

---

## PostgreSQL Databases

### 1. Identity Database: `tansu_identity`

**Purpose:** Stores ASP.NET Core Identity users, roles, and OpenIddict OIDC configuration (clients, scopes, tokens, authorizations).

**Schema Version:** `v1.0.0` (no EF migrations yet; managed via dev init script)

**Required Tables:**

| Table Name | Purpose | Key Indexes |
|------------|---------|-------------|
| `AspNetUsers` | User accounts with email, password hash, security stamp | PK: `Id`, Index: `NormalizedEmail`, `NormalizedUserName` |
| `AspNetRoles` | Roles (Admin, Tenant) | PK: `Id`, Index: `NormalizedName` |
| `AspNetUserRoles` | User-to-role mapping | Composite PK: `UserId, RoleId` |
| `AspNetUserClaims` | Custom user claims | PK: `Id`, FK: `UserId` |
| `AspNetUserLogins` | External login providers | Composite PK: `LoginProvider, ProviderKey` |
| `AspNetUserTokens` | Authentication tokens | Composite PK: `UserId, LoginProvider, Name` |
| `AspNetRoleClaims` | Role claims | PK: `Id`, FK: `RoleId` |
| `OpenIddictApplications` | OIDC clients (Dashboard, external apps) | PK: `Id`, Index: `ClientId` |
| `OpenIddictAuthorizations` | User consent grants | PK: `Id`, FK: `ApplicationId, Subject` |
| `OpenIddictScopes` | OIDC scopes (openid, profile, db.read, db.write, storage.read, storage.write) | PK: `Id`, Index: `Name` |
| `OpenIddictTokens` | Access/refresh/id tokens | PK: `Id`, FK: `ApplicationId, AuthorizationId`, Index: `ReferenceId` |

**Required Extensions:**

- `citus` (distributed PostgreSQL; enables sharding if needed)
- `pg_trgm` (trigram indexing for fuzzy search)

**Dev Init Script:** `dev/db-init/10-identity.sql`

**Managed By:** Manual SQL scripts; no EF Core migrations currently (future enhancement)

**Health Check:** `/identity/health/ready` (application health, not schema validation)

---

### 2. Audit Database: `tansu_audit`

**Purpose:** Compliance audit trail for all security-relevant operations (tenant provisioning, user actions, policy changes, schema updates).

**Schema Version:** `v1.0.0` (initial migration)

**Status:** ❌ **Does not exist yet** (blocked Task 31 completion)

**Required Tables:**

| Table Name | Purpose | Key Indexes |
|------------|---------|-------------|
| `audit_events` | Audit event log with immutable records | PK: `Id` (UUID v7), Index: `WhenUtc DESC`, `TenantId`, `Category`, `Action`, `Service` |
| `__AuditSchemaVersion` | Schema version tracking | PK: `Component`, Index: `AppliedAt DESC` |

**audit_events Schema:**

```sql
CREATE TABLE audit_events (
    Id UUID PRIMARY KEY,              -- UUID v7 for time-ordered IDs
    WhenUtc TIMESTAMPTZ NOT NULL,     -- Event timestamp (UTC)
    TenantId TEXT,                    -- Tenant identifier (null for system events)
    Category TEXT NOT NULL,           -- Event category (e.g., "authentication", "database.provisioning")
    Action TEXT NOT NULL,             -- Specific action (e.g., "login.success", "tenant.create")
    Service TEXT NOT NULL,            -- Source service (e.g., "identity", "database", "storage")
    Subject TEXT,                     -- User ID or system identifier
    CorrelationId TEXT,               -- Request correlation ID (X-Correlation-ID)
    Metadata JSONB,                   -- Additional context (no PII)
    TraceId TEXT,                     -- OpenTelemetry trace ID
    SpanId TEXT                       -- OpenTelemetry span ID
);

CREATE INDEX idx_audit_events_when ON audit_events (WhenUtc DESC);
CREATE INDEX idx_audit_events_tenant ON audit_events (TenantId) WHERE TenantId IS NOT NULL;
CREATE INDEX idx_audit_events_category ON audit_events (Category);
CREATE INDEX idx_audit_events_action ON audit_events (Action);
CREATE INDEX idx_audit_events_service ON audit_events (Service);
CREATE INDEX idx_audit_events_correlation ON audit_events (CorrelationId) WHERE CorrelationId IS NOT NULL;
```

**Required Extensions:** None (standard PostgreSQL)

**Managed By:** `TansuCloud.Audit` project with EF Core migrations (to be created in Phase 1)

**Health Check:** Schema validation in `DatabaseSchemaHostedService`

---

### 3. Tenant Databases: `tansu_tenant_{tenant_id}`

**Purpose:** Per-tenant document storage with collections, JSONB documents, and vector embeddings for semantic search.

**Schema Version:** `v1.0.0` (initial provisioning schema)

**Naming Convention:** `tansu_tenant_` + normalized tenant ID (non-alphanumeric chars → `_`)

**Examples:**

- Tenant ID `acme-dev` → Database `tansu_tenant_acme_dev`
- Tenant ID `globex.corp` → Database `tansu_tenant_globex_corp`

**Required Tables:**

| Table Name | Purpose | Key Indexes |
|------------|---------|-------------|
| `documents` | JSONB documents with metadata | PK: `id` (TEXT), FK: `collection_id`, Index: `created_at DESC`, `updated_at DESC`, GIN index on `content` |
| `collections` | Document collections (schemas/namespaces) | PK: `id` (TEXT), Index: `name` (unique), `created_at DESC` |
| `vector_embeddings` | Vector embeddings for semantic search (1536 dimensions) | PK: `id` (UUID), FK: `document_id`, HNSW index on `embedding` |
| `__TenantSchemaVersion` | Schema version tracking | PK: `Component`, Index: `AppliedAt DESC` |

**documents Schema:**

```sql
CREATE TABLE documents (
    id TEXT PRIMARY KEY,
    collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    content JSONB NOT NULL,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    etag TEXT NOT NULL,
    row_version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX idx_documents_collection ON documents (collection_id);
CREATE INDEX idx_documents_created ON documents (created_at DESC);
CREATE INDEX idx_documents_updated ON documents (updated_at DESC);
CREATE INDEX idx_documents_content_gin ON documents USING GIN (content);
```

**collections Schema:**

```sql
CREATE TABLE collections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    description TEXT,
    schema JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_collections_name ON collections (name);
CREATE INDEX idx_collections_created ON collections (created_at DESC);
```

**vector_embeddings Schema:**

```sql
CREATE TABLE vector_embeddings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    embedding vector(1536) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_vector_embeddings_document ON vector_embeddings (document_id);
CREATE INDEX idx_vector_embeddings_collection ON vector_embeddings (collection_id);

-- HNSW index for fast approximate nearest neighbor search
CREATE INDEX idx_vector_embeddings_hnsw ON vector_embeddings 
    USING hnsw (embedding vector_cosine_ops);
```

**Required Extensions:**

- `citus` (distributed PostgreSQL)
- `vector` (pgvector for vector embeddings; 1536 dimensions for OpenAI embeddings)
- `pg_trgm` (trigram indexing for fuzzy text search)

**Extension Version Requirements:**

- `citus`: >= 12.0
- `vector`: >= 0.5.0 (for HNSW indexing)
- `pg_trgm`: any stable version

**Managed By:**

- Database created by: `TansuCloud.Database/Provisioning/TenantProvisioningService.cs`
- Extensions updated by: `TansuCloud.Database/Services/ExtensionVersionService.cs` (Task 11 follow-up)
- Schema initialized by: Provisioning service (runs CREATE TABLE IF NOT EXISTS on provision)

**Health Check:**

- Extension versions: `/db/health/ready` (operational since Task 11 follow-up)
- Schema validation: `DatabaseSchemaHostedService` (to be implemented in Phase 2)

---

## ClickHouse Databases (SigNoz)

### Overview

SigNoz manages its own ClickHouse schemas via the `signoz-schema-migrator` container. TansuCloud performs **read-only connectivity checks only** and does not manage SigNoz schemas.

**Managed By:** SigNoz schema migrator container (runs automatically on startup)

**TansuCloud Responsibility:** Validate connectivity and table presence for health checks

### Required Databases

#### 1. `signoz_traces`

**Purpose:** Distributed tracing data (OpenTelemetry spans)

**Key Tables:**

| Table Name | Purpose | Notes |
|------------|---------|-------|
| `distributed_signoz_index_v3` | Main trace index (distributed) | Query target for trace searches |
| `signoz_spans` | Span storage (local) | Raw span data |
| `signoz_error_index_v2` | Error event index | Fast error lookup |
| `durationSortMV` | Duration materialized view | Pre-aggregated for performance |

**Validation Query:** `SELECT 1 FROM signoz_traces.distributed_signoz_index_v3 LIMIT 1`

#### 2. `signoz_metrics`

**Purpose:** Application metrics (counters, histograms, gauges)

**Key Tables:**

| Table Name | Purpose | Notes |
|------------|---------|-------|
| `distributed_samples_v2` | Metrics samples (distributed) | Time-series data points |
| `time_series_v2` | Metric metadata | Labels and series info |

**Validation Query:** `SELECT 1 FROM signoz_metrics.distributed_samples_v2 LIMIT 1`

#### 3. `signoz_logs`

**Purpose:** Structured log storage (OTEL logs protocol)

**Key Tables:**

| Table Name | Purpose | Notes |
|------------|---------|-------|
| `distributed_logs` | Log records (distributed) | Full-text searchable |
| `logs` | Log storage (local) | Raw log data |

**Validation Query:** `SELECT 1 FROM signoz_logs.distributed_logs LIMIT 1`

### Health Check Strategy

- **Type:** Informational only (does not fail startup if unreachable)
- **Method:** TCP connectivity check to ClickHouse port (9000) + simple SELECT query
- **Reported In:** `/db/health/ready` under `clickhouse_reachable` field
- **Rationale:** SigNoz may be in separate network segment or disabled in dev; should not block service startup

---

## PgCat Pool Configuration

### Overview

PgCat acts as a connection pooler between services and PostgreSQL. Each tenant database requires a corresponding PgCat pool for efficient connection management.

**Config File:** `/etc/pgcat/pgcat.toml` (inside `tansu-pgcat` container)

**Admin Interface:** Port 9930 (SHOW POOLS, SHOW STATS commands)

### Required Pools

| Pool Name | Database | Purpose |
|-----------|----------|---------|
| `identity` | `tansu_identity` | Identity/OIDC queries |
| `audit` | `tansu_audit` | Audit event writes (after audit DB created) |
| `tenant_{tenant_id}` | `tansu_tenant_{tenant_id}` | Per-tenant document/vector queries |

**Example Pool Configuration:**

```toml
[pools.identity]
pool_mode = "transaction"
default_role = "any"
max_pool_size = 50
min_pool_size = 5
connect_timeout = 5000

[pools.identity.users.0]
username = "postgres"
password = "postgres"
pool_size = 25

[pools.tenant_acme_dev]
pool_mode = "transaction"
default_role = "any"
max_pool_size = 50
min_pool_size = 5
connect_timeout = 5000

[pools.tenant_acme_dev.users.0]
username = "postgres"
password = "postgres"
pool_size = 25
```

### Current Management Process

1. **Tenant Provisioning:** When a new tenant is created via `/db/api/provisioning/tenants`, the provisioning service:
   - Creates the PostgreSQL database
   - Appends pool config to `/etc/pgcat/pgcat.toml` via `docker exec`
   - Sends `SIGHUP` to PgCat to reload config

2. **Manual Verification:**

   ```bash
   docker exec tansu-pgcat psql -h localhost -p 6432 -U postgres -d postgres -c "SHOW POOLS;"
   ```

### Known Issues

- ⚠️ No reconciliation loop: If PgCat restarts or config is lost, pools must be manually re-added
- ⚠️ No drift detection: No validation that all tenant databases have corresponding pools
- ⚠️ Race conditions: Provisioning may succeed but PgCat reload may fail silently

### Phase 3 Enhancement (PgCatPoolHostedService)

Will implement:

- Automatic pool discovery from PostgreSQL
- Drift detection (missing pools)
- Automatic pool addition with idempotent reload
- Health check reporting (`/db/health/ready` includes pool status)

---

## Schema Version Tracking

### Purpose

Track which schema version each database component is running to prevent incompatible deployments and guide upgrade paths.

### Tracking Table: `__SchemaVersion`

**Schema:**

```sql
CREATE TABLE IF NOT EXISTS __SchemaVersion (
    Component TEXT PRIMARY KEY,        -- e.g., "Identity", "Tenant", "Audit"
    Version TEXT NOT NULL,             -- Semantic version (e.g., "1.0.0") or migration ID
    AppliedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    AppliedBy TEXT,                    -- Service/user that applied the version
    Description TEXT                   -- Human-readable change summary
);
```

**Component Names (standardized):**

- `Identity` - tansu_identity schema version
- `Tenant` - tansu_tenant_* schema version
- `Audit` - tansu_audit schema version

### Version Format

Use semantic versioning: `MAJOR.MINOR.PATCH`

- **MAJOR:** Breaking schema changes (requires data migration)
- **MINOR:** Backward-compatible additions (new tables/columns)
- **PATCH:** Fixes, index optimizations (no schema changes)

**Examples:**

- `1.0.0` - Initial schema
- `1.1.0` - Added vector_embeddings table
- `2.0.0` - Breaking change: documents.content → documents.data

### Validation Rules

At startup, `SchemaVersionService` will:

1. Read `__SchemaVersion` from each database
2. Compare with expected version (hardcoded in service or config)
3. Determine compatibility:
   - **Same MAJOR version:** Compatible
   - **Different MAJOR version:** Incompatible (fail startup)
   - **MINOR mismatch:** Warn but allow (backward-compatible)

### Phase 1 Implementation

Create `TansuCloud.Database/Services/SchemaVersionService.cs` with methods:

```csharp
Task<string?> GetCurrentVersionAsync(string dbName, string component);
Task RecordVersionAsync(string dbName, string component, string version, string? description = null);
Task<bool> ValidateVersionAsync(string dbName, string component, string expectedVersion);
```

---

## Extension Requirements

### PostgreSQL Extensions

| Extension | Required Version | Purpose | Update Policy |
|-----------|------------------|---------|---------------|
| `citus` | >= 12.0 | Distributed PostgreSQL | Auto-update on startup (Task 11 follow-up) |
| `vector` | >= 0.5.0 | pgvector for embeddings | Auto-update on startup (Task 11 follow-up) |
| `pg_trgm` | Any stable | Trigram text search | No version constraint |

### Extension Version Checks

✅ **Operational since Task 11 follow-up (2025-10-07)**

**Managed By:** `TansuCloud.Database/Services/ExtensionVersionService.cs`

**Behavior:**

- Runs on Database service startup
- Queries extension versions in all tenant databases
- Automatically updates `citus` and `vector` to latest if below minimum
- Logs all updates to audit trail
- Reports versions in `/db/health/ready`

**Can Be Disabled:** `SKIP_EXTENSION_UPDATE=true`

**Health Check:** `/db/health/ready` includes extension versions under `extensions` field

---

## Startup Validation Requirements

### DatabaseSchemaHostedService (Phase 2)

Validates infrastructure before accepting HTTP traffic. Runs immediately after `ExtensionVersionHostedService` in the startup sequence.

**Validation Steps:**

1. **Identity Database**
   - Check `tansu_identity` exists
   - Verify expected tables exist (AspNetUsers, OpenIddictApplications, etc.)
   - Read schema version from `__SchemaVersion` table (if present)
   - Validate version compatibility

2. **Audit Database**
   - Check `tansu_audit` exists
   - Verify `audit_events` table exists
   - Read schema version
   - Log warning if missing in Development (auto-create if `ENABLE_AUTO_AUDIT_DB_INIT=true`)
   - Fail startup in Production if missing

3. **Tenant Databases**
   - Discover all `tansu_tenant_*` databases
   - For each tenant database:
     - Verify required tables exist (documents, collections, vector_embeddings)
     - Check extensions are installed (citus, vector, pg_trgm)
     - Read schema version
   - Log summary: `Validated {TenantCount} tenant databases`

4. **ClickHouse Connectivity (Informational)**
   - Attempt TCP connection to ClickHouse
   - Run simple probe query: `SELECT 1 FROM signoz_traces.distributed_signoz_index_v3 LIMIT 1`
   - Log status but **do not fail startup** if unreachable
   - Report status in health check

**Failure Modes:**

- **Production:** Fail-fast if any critical validation fails (Identity, Audit, Tenant schemas)
- **Development:** Log warnings but continue (tolerance for missing dependencies)

**Configuration:**

| Variable | Default | Description |
|----------|---------|-------------|
| `SKIP_SCHEMA_VALIDATION` | `false` | Disable all schema validation |
| `SCHEMA_VALIDATION_TIMEOUT_SEC` | `60` | Max time before failing |
| `ENABLE_AUTO_AUDIT_DB_INIT` | `true` in Dev, `false` in Prod | Auto-create audit DB if missing |

---

## Migration Procedures

### Adding a New Tenant Database

**Triggered By:** `POST /db/api/provisioning/tenants`

**Process:**

1. Normalize tenant ID to database name
2. Create PostgreSQL database: `CREATE DATABASE tansu_tenant_{tenant_id}`
3. Enable extensions: `CREATE EXTENSION IF NOT EXISTS citus, vector, pg_trgm`
4. Create tables: documents, collections, vector_embeddings
5. Initialize schema version: `INSERT INTO __SchemaVersion VALUES ('Tenant', '1.0.0', NOW())`
6. Add PgCat pool (via `docker exec` + SIGHUP)
7. Log audit event: `database.provisioning.tenant.create`

**Idempotent:** Can be called multiple times; skips if database already exists

**Validation:** `DatabaseSchemaHostedService` detects new tenant on next startup

### Upgrading Identity Schema

**Status:** ❌ No EF Core migrations yet; manual SQL scripts required

**Future Process (when EF migrations are added):**

1. Stop Database service
2. Backup `tansu_identity` database
3. Run migrations: `dotnet ef database update --project TansuCloud.Identity`
4. Update schema version: `UPDATE __SchemaVersion SET Version = '2.0.0' WHERE Component = 'Identity'`
5. Restart Database service
6. Startup validation confirms new version

### Creating Audit Database (Phase 1)

**One-Time Setup:**

1. Create `TansuCloud.Audit` project with EF Core
2. Define `AuditEvent` entity
3. Add migration: `dotnet ef migrations add InitialAuditSchema --project TansuCloud.Audit`
4. Apply migration: `dotnet ef database update --project TansuCloud.Audit`
5. Initialize schema version: `INSERT INTO __SchemaVersion VALUES ('Audit', '1.0.0', NOW())`

**Development Auto-Init:**

If `ENABLE_AUTO_AUDIT_DB_INIT=true`, `DatabaseSchemaHostedService` will:

- Detect missing `tansu_audit` database
- Run `context.Database.EnsureCreatedAsync()`
- Initialize schema version
- Log event

### Upgrading Tenant Schemas

**Future Process (when schema changes are needed):**

1. Define new schema version (e.g., `1.1.0` adds new table)
2. Create migration SQL script: `migrations/tenant-v1.1.0.sql`
3. Update `SchemaVersionService` expected version: `1.1.0`
4. Deploy new Database service
5. On startup, `DatabaseSchemaHostedService` will:
   - Detect tenant schemas at `1.0.0`
   - Log warning: "Tenant schemas need upgrade to 1.1.0"
   - Fail startup in Production (force manual upgrade)
   - Continue in Development with warning

**Manual Upgrade Process:**

```bash
# For each tenant database
psql -h localhost -p 5432 -U postgres -d tansu_tenant_acme_dev < migrations/tenant-v1.1.0.sql

# Update version tracking
psql -h localhost -p 5432 -U postgres -d tansu_tenant_acme_dev -c \
  "UPDATE __SchemaVersion SET Version = '1.1.0', AppliedAt = NOW() WHERE Component = 'Tenant'"
```

---

## Compatibility Matrix

### Database Service vs Schema Versions

| Database Service Version | Identity Schema | Tenant Schema | Audit Schema | Notes |
|--------------------------|----------------|---------------|--------------|-------|
| `1.0.0` | `1.0.0` | `1.0.0` | `1.0.0` | Initial release |
| `1.1.0` | `1.0.0` | `1.1.0` | `1.0.0` | Added vector HNSW indexes |
| `2.0.0` | `2.0.0` | `2.0.0` | `1.0.0` | Breaking: EF migrations for Identity |

### Extension Version Compatibility

| Extension | Minimum Required | Recommended | Notes |
|-----------|------------------|-------------|-------|
| `citus` | `12.0` | `12.1+` | Auto-updated on startup |
| `vector` | `0.5.0` | `0.7.0+` | Requires HNSW support |
| `pg_trgm` | Any | Latest | No breaking changes expected |

### PostgreSQL Version Support

- **Minimum:** PostgreSQL 14
- **Recommended:** PostgreSQL 17 (current dev image: `citusdata/citus:12.1-pg17`)
- **Tested:** PostgreSQL 17 with Citus 12.1

### ClickHouse Version Support

- **Managed By:** SigNoz (no direct dependency)
- **Validation:** Read-only connectivity check only
- **Minimum:** ClickHouse 23.x (SigNoz requirement)

---

## Related Documentation

- [Architecture.md](../Architecture.md) - Overall system architecture
- [Guide-For-Admins-and-Tenants.md](../Guide-For-Admins-and-Tenants.md) - Operational procedures
- [Tasks-M4.md](../Tasks-M4.md) - Task 43 details
- [TelemetryDeployment.md](./TelemetryDeployment.md) - SigNoz deployment guide

---

## Revision History

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2025-10-07 | 1.0.0 | Initial comprehensive schema documentation | Task 43 Phase 1 |

---

**Next Steps:**

1. ✅ Schema documentation complete
2. ⏳ Create TansuCloud.Audit project with EF Core migrations (Phase 1)
3. ⏳ Implement SchemaVersionService (Phase 1)
4. ⏳ Create DatabaseSchemaHostedService (Phase 2)
5. ⏳ Implement PgCatPoolHostedService (Phase 3)
6. ⏳ Extend health checks (Phase 4)
7. ⏳ Update operational documentation (Phase 5)
