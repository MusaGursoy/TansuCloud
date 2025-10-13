# Production Deployment Analysis: docker-compose.prod.yml

## Question

If an end user runs `docker-compose.prod.yml`, will all necessary databases and tables be created in PostgreSQL and ClickHouse, and will PgCat pools be created?

## Short Answer

**YES (as of 2025-10-08) - MOSTLY AUTOMATIC.** Core infrastructure databases are created automatically on first start. Only tenant databases require manual provisioning via API (by design).

---

## Detailed Analysis

### What IS Created Automatically ✅

#### 1. PostgreSQL: Identity Database

**Database:** `tansu_identity`  
**Created by:** PostgreSQL init scripts (`dev/db-init/01-init.sql`)  
**When:** On first container start (empty data volume)  
**Tables:**

- OpenIddict tables (applications, scopes, tokens, authorizations)
- ASP.NET Identity tables (AspNetUsers, AspNetRoles, etc.)

**Status:** ✅ **AUTOMATIC**

#### 2. PostgreSQL: Audit Database

**Database:** `tansu_audit`  
**Created by:** PostgreSQL init scripts (`dev/db-init/01-init.sql`)  
**When:** On first container start (empty data volume)  
**Tables:**

- `audit_events` - Created by EF Core migrations (TansuCloud.Audit)
- `__SchemaVersion` - Created by SchemaVersionService

**Status:** ✅ **AUTOMATIC** (database created by init script, tables created by EF migrations)

#### 3. PostgreSQL: Extensions

**Extensions installed:**

- `citus` - Distributed PostgreSQL
- `vector` - pgvector for embeddings
- `pg_trgm` - Trigram text search

**Where:** `postgres` database, `template1`, `tansu_identity`, and `tansu_audit`  
**Created by:** Init scripts  
**Status:** ✅ **AUTOMATIC**

#### 4. ClickHouse: SigNoz Tables (if observability profile enabled)

**Databases:**

- `signoz_traces`
- `signoz_metrics`
- `signoz_logs`

**Created by:**

- `signoz-schema-migrator-sync` container
- `signoz-schema-migrator-async` container
- `clickhouse-prepatch` container
- `clickhouse-compat-init` container

**When:** After ClickHouse is healthy  
**Status:** ✅ **AUTOMATIC** (only if `--profile observability` is used)

**Command:**

```bash
docker compose -f docker-compose.prod.yml --profile observability up -d --build
```

---

### What IS NOT Created Automatically ❌

#### 1. PostgreSQL: Tenant Databases

**Databases:** `tansu_tenant_{tenant_id}` (e.g., `tansu_tenant_acme`)  
**Tables:** `documents`, `collections`, `vector_embeddings`, `tenant_info`  
**Created by:** Provisioning API (`POST /db/api/provisioning/tenants`)

**Current state:**

- ❌ NOT created by init scripts
- ❌ NOT created on Database service startup
- ✅ Created via API call to provisioning endpoint

**Required action:**

```bash
# Call provisioning API for each tenant
curl -X POST http://your-gateway/db/api/provisioning/tenants \
  -H "X-Provision-Key: your-key" \
  -H "Content-Type: application/json" \
  -d '{
    "tenantId": "acme",
    "displayName": "Acme Corporation"
  }'
```

**Status:** ❌ **MANUAL PROVISIONING REQUIRED**

#### 2. PgCat: Tenant Pools

**Pools:** One pool per tenant database  
**Created by:** `pgcat-config` service (Dev.PgcatConfigurator)

**Current state:**

- ✅ `pgcat-config` service runs on startup
- ✅ Discovers tenant databases from PostgreSQL
- ✅ Adds missing pools to PgCat configuration
- ❌ **BUT:** No tenant databases exist initially, so no pools are created

**Reconciliation:**

- ✅ When a tenant is provisioned → API adds tenant DB → `PgCatPoolHostedService` reconciles pools
- ✅ Periodic reconciliation ensures pools stay in sync

**Status:** ⚠️ **AUTOMATIC AFTER TENANTS ARE PROVISIONED**

---

## Production Deployment Checklist

### Step 1: Start Core Infrastructure

```bash
cd TansuCloud
docker compose -f docker-compose.prod.yml up -d postgres redis pgcat pgcat-config
```

Wait for all services to be healthy.

**What happens automatically:**
- ✅ PostgreSQL starts and runs init scripts
- ✅ `tansu_identity` database created (by init script)
- ✅ `tansu_audit` database created (by init script)  
- ✅ Extensions installed in both databases
- ✅ Redis starts
- ✅ PgCat starts and loads configuration
- ✅ PgCat configurator runs (no tenants to reconcile yet)

### Step 2: Start Application Services

```bash
docker compose -f docker-compose.prod.yml up -d identity dashboard db storage gateway
```

**Expected behavior:**
- ✅ Identity starts (uses `tansu_identity` database)
- ✅ Database service validates `tansu_identity` and `tansu_audit` exist (both pass ✅)
- ✅ All services can write audit events to `tansu_audit`
- ⚠️ No tenant databases exist yet (expected - requires provisioning)

### Step 3: (Optional) Start Observability Stack

```bash
docker compose -f docker-compose.prod.yml --profile observability up -d
```

This starts:

- ZooKeeper
- ClickHouse
- SigNoz UI
- SigNoz OTEL Collector
- Schema migrators

**Expected behavior:**

- ✅ ClickHouse tables created automatically by migrators
- ✅ Services can send telemetry to OTEL collector
- ✅ SigNoz UI available (internal only, no host port exposed by default)

### Step 4: Provision Tenants

```bash
# For each tenant, call the provisioning API
curl -X POST http://your-gateway/db/api/provisioning/tenants \
  -H "X-Provision-Key: your-provision-key" \
  -H "Content-Type: application/json" \
  -d '{
    "tenantId": "acme",
    "displayName": "Acme Corporation"
  }'
```

**What happens:**

1. Database service creates `tansu_tenant_acme`
2. Installs extensions: `citus`, `vector`, `pg_trgm`
3. Runs EF Core migrations (creates tables: `documents`, `collections`, etc.)
4. PgCat configurator detects new tenant DB
5. Adds pool to PgCat configuration
6. Reloads PgCat

**Expected behavior:**

- ✅ Tenant database created with correct schema
- ✅ PgCat pool added and activated
- ✅ Tenant can access their database via API

---

## Summary Table

| Component | Auto-Created? | When | Action Required |
|-----------|--------------|------|-----------------|
| **PostgreSQL: Identity DB** | ✅ YES | First start (init scripts) | None |
| **PostgreSQL: Audit DB** | ✅ YES | First start (init scripts) | None ✅ **FIXED (2025-10-08)** |
| **PostgreSQL: Tenant DBs** | ❌ NO | - | **Call provisioning API per tenant** |
| **PostgreSQL: Extensions** | ✅ YES | First start (init scripts) | None |
| **ClickHouse: SigNoz tables** | ✅ YES | When observability profile enabled | Enable `--profile observability` |
| **PgCat: Pools** | ⚠️ AFTER TENANTS | When tenant DBs exist | Automatic after tenant provisioning |

---

## Recommendations for Production-Ready Setup

### 1. Audit Database Init Script ✅ IMPLEMENTED (2025-10-08)

**Status:** ✅ **COMPLETE** - `tansu_audit` database is now created automatically by init script

**File:** `dev/db-init/01-init.sql`

**What was added:**

```sql
-- Audit DB (used by all services for audit logging)
CREATE DATABASE tansu_audit;

\connect tansu_audit
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
\connect postgres
```

**Benefit:** Zero-touch deployment - audit database exists on first start

### 2. Document Tenant Provisioning in Runbook ✅ COMPLETED

Create operational runbook for:

- How to provision new tenants
- How to verify PgCat pools were created
- How to manually reconcile pools if drift occurs

**Already exists:** `Guide-For-Admins-and-Tenants.md` section 5

### 3. Consider Init Container Pattern for Audit DB ✅ OPTIONAL

Instead of relying on PostgreSQL init scripts, create a dedicated init container that:

1. Waits for PostgreSQL to be ready
2. Checks if `tansu_audit` exists
3. Creates it if missing
4. Runs EF Core migrations (TansuCloud.Audit)
5. Exits successfully

**Benefit:** More explicit, works even if data volume is not empty

**Example:**

```yaml
audit-init:
  image: mcr.microsoft.com/dotnet/sdk:9.0
  container_name: tansu-audit-init
  depends_on:
    postgres:
      condition: service_started
  networks:
    - tansucloud-network
  volumes:
    - ./TansuCloud.Audit:/app
  working_dir: /app
  environment:
    ConnectionStrings__Default: "Host=postgres;Port=5432;Database=tansu_audit;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
  command: |
    bash -c '
      echo "Waiting for PostgreSQL..."
      until psql -h postgres -U ${POSTGRES_USER} -c "SELECT 1" > /dev/null 2>&1; do sleep 1; done
      echo "Creating tansu_audit database if missing..."
      psql -h postgres -U ${POSTGRES_USER} -tc "SELECT 1 FROM pg_database WHERE datname = '\''tansu_audit'\''" | grep -q 1 || psql -h postgres -U ${POSTGRES_USER} -c "CREATE DATABASE tansu_audit;"
      echo "Running EF Core migrations..."
      dotnet ef database update
      echo "Audit database ready."
    '
```

### 4. Pre-Provision Common Tenants ✅ OPTIONAL

For known tenants (e.g., "acme-dev" for development), consider:

- Adding tenant provisioning to init container
- Or documenting in deployment checklist
- Or providing a "bootstrap" script that calls provisioning API

---

## Current Gaps and Risks (Updated 2025-10-08)

### ✅ RESOLVED: Audit Database Not Created Automatically

**Previous risk:** Database service failed to start in production  
**Impact:** High - blocked entire deployment  
**Resolution:** Added to init script (`dev/db-init/01-init.sql`)  
**Status:** ✅ **FIXED** - `tansu_audit` now created automatically

### ⚠️ Gap 1: No Tenant Databases on First Start

**Risk:** No data storage available until tenants are provisioned  
**Impact:** Medium - expected behavior, documented in guide  
**Mitigation:** Documented in deployment guide and `Guide-For-Admins-and-Tenants.md`  
**Status:** ✅ Acceptable (by design)

### ⚠️ Gap 2: No Default Tenant

**Risk:** Manual provisioning required for first tenant  
**Impact:** Low - one-time setup per environment  
**Mitigation:** Provide provisioning script or bootstrap container  
**Status:** Acceptable (can improve in future)

---

## Conclusion (Updated 2025-10-08)

**Running `docker-compose.prod.yml` now provides a FULLY OPERATIONAL core infrastructure. ✅**

**What you get automatically:**
- ✅ Identity database (`tansu_identity`) - automatic
- ✅ Audit database (`tansu_audit`) - automatic ✅ **FIXED**
- ✅ PostgreSQL extensions (`citus`, `vector`, `pg_trgm`) - automatic
- ✅ ClickHouse/SigNoz tables - automatic (if observability profile enabled)

**What you must do manually (by design):**
1. ⚠️ Provision tenant databases via API - **EXPECTED** (multi-tenancy by design)
2. ✅ PgCat pools auto-reconcile after tenant provisioning - automatic

**Deployment is now "zero-touch" for core infrastructure. 🎉**

### Simplified Production Deployment

```bash
# 1. Start all core services
docker compose -f docker-compose.prod.yml up -d

# 2. (Optional) Enable observability
docker compose -f docker-compose.prod.yml --profile observability up -d

# 3. Provision tenants as needed
curl -X POST http://gateway/db/api/provisioning/tenants \
  -H "X-Provision-Key: your-key" \
  -H "Content-Type: application/json" \
  -d '{"tenantId": "acme", "displayName": "Acme Corp"}'
```

**Result:** All core databases exist and are validated. Services start successfully. Tenant provisioning is the only manual step (as intended for multi-tenant architecture).

