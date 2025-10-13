// Tansu.Cloud Public Repository:    <https://github.com/MusaGursoy/TansuCloud>

# Database Container Upgrades: PostgreSQL Extension Management

**Document version**: 1.1  
**Last updated**: 2025-10-07  
**Audience**: DevOps, SREs, Database Administrators

> **✅ Implementation Status (2025-10-07)**: TansuCloud Database service now includes **automatic extension pre-flight checks** on startup (Option 2 below). The `ExtensionVersionService` automatically updates Citus and pgvector extensions in all tenant databases before accepting traffic. This prevents `XX000` errors after image upgrades. See `TansuCloud.Database/Services/ExtensionVersionService.cs` for implementation.

## Executive Summary

TansuCloud uses a custom PostgreSQL image (`tansu/citus-pgvector`) with Citus (distributed database) and pgvector (vector similarity) extensions. When upgrading the container image, the **shared libraries** update immediately but **extension metadata** in existing databases remains at old versions, causing `XX000` errors and complete database write failures.

**Critical**: Production upgrades require explicit extension update commands (`ALTER EXTENSION ... UPDATE`) on every tenant database, or services will fail with `500 InternalServerError`.

**✅ Automated Solution**: The Database service now handles this automatically via pre-flight checks on startup. This document provides context, alternative strategies, and operational guidance.

This document provides:

1. Understanding the problem
2. Development workflow
3. Production upgrade strategies (pinned versions, pre-flight checks, blue-green deployment)
4. Rollback procedures
5. Monitoring and validation

## Table of Contents

- [The Problem](#the-problem)
- [Why This Happens](#why-this-happens)
- [Impact](#impact)
- [Development Workflow](#development-workflow)
- [Production Strategies](#production-strategies)
  - [1. Version Pinning (Mandatory)](#1-version-pinning-mandatory)
  - [2. Pre-flight Checks (Recommended)](#2-pre-flight-checks-recommended)
  - [3. Blue-Green Deployment (Zero-Downtime)](#3-blue-green-deployment-zero-downtime)
  - [4. Migration-Based Updates](#4-migration-based-updates)
  - [5. Health Checks](#5-health-checks)
- [Rollback Procedures](#rollback-procedures)
- [Monitoring](#monitoring)
- [FAQ](#faq)

## The Problem

### Scenario

You rebuild the `tansu/citus-pgvector` Docker image to upgrade Citus from version 13.1 to 13.2:

```dockerfile
FROM citusdata/citus:latest  # Now pulls Citus 13.2
```

After deploying the new container:

- **Symptom**: All database writes fail with `500 InternalServerError`
- **Logs**: `PostgresException (0x80004005): XX000: loaded Citus library version differs from installed extension version`
- **Impact**: Complete database service outage until resolved

### Why This Happens

PostgreSQL extensions have two components:

1. **Shared library** (`/usr/lib/postgresql/.../citus.so`) — updated when Docker image rebuilds
2. **Extension metadata** (`pg_extension` catalog) — remains at old version until explicitly upgraded

When versions mismatch, PostgreSQL refuses database operations to prevent data corruption.

### Impact

- **Write operations**: All INSERTs, UPDATEs, DELETEs fail immediately
- **Read operations**: May work initially but fail on transaction start
- **Services**: Database, Storage APIs return 500 errors
- **Users**: Complete service outage for all tenants

**Mean Time to Recover (MTTR)**: 5-15 minutes (manual extension updates + service restarts)

## Development Workflow

### Current State (Fast Iteration)

Development uses `:latest` tags for rapid testing:

```dockerfile
FROM citusdata/citus:latest
RUN apt-get install -y postgresql-17-pgvector
```

### After Rebuilding Image

1. **Identify affected databases**:

   ```powershell
   docker exec tansu-postgres psql -U postgres -c "\l" | Select-String "tansu_tenant"
   ```

2. **Update extensions in each database**:

   ```powershell
   $dbs = @('tansu_tenant_acme_dev', 'tansu_tenant_e2e_server_ank')
   foreach ($db in $dbs) {
       docker exec tansu-postgres psql -U postgres -d $db -c "
           ALTER EXTENSION citus UPDATE;
           ALTER EXTENSION vector UPDATE;
           SELECT extname, extversion FROM pg_extension WHERE extname IN ('citus', 'vector');
       "
   }
   ```

3. **Restart connection pooler and services**:

   ```powershell
   docker compose restart pgcat db storage
   ```

4. **Verify**:

   ```powershell
   dotnet test .\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj --filter VectorSearchE2E
   ```

## Production Strategies

### 1. Version Pinning (Mandatory)

**Never use `:latest` in production**. Pin explicit versions:

```dockerfile
# Production: dev/Dockerfile.citus-pgvector.prod
FROM citusdata/citus:12.1-pg16

USER root
RUN set -eux; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        postgresql-16-pgvector=0.8.0-1 \
    && rm -rf /var/lib/apt/lists/*

USER postgres
```

**Tag images with versions**:

```bash
docker build -f dev/Dockerfile.citus-pgvector.prod \
  -t yourorg/citus-pgvector:citus12.1-pg16-pgvector0.8.0 .

docker push yourorg/citus-pgvector:citus12.1-pg16-pgvector0.8.0
```

**Rationale**: You control when upgrades happen, after thorough testing in staging.

### 2. Pre-flight Checks (✅ IMPLEMENTED)

**Status**: This strategy has been fully implemented in the TansuCloud Database service.

**Implementation**: 
- `TansuCloud.Database/Services/ExtensionVersionService.cs` - Core service for extension updates
- `TansuCloud.Database/Hosting/ExtensionVersionHostedService.cs` - Hosted service wrapper
- Registered in `TansuCloud.Database/Program.cs`

**How it works**:
- Runs automatically on Database service startup (before accepting HTTP traffic)
- Discovers all tenant databases (`tansu_tenant_*`)
- Updates Citus and pgvector extensions via `ALTER EXTENSION UPDATE`
- Logs version transitions (e.g., `vector 0.8.0 → 0.8.1`)
- Fails startup in production if updates fail; tolerates errors in development

**Configuration**:
- Default: Enabled automatically, no configuration required
- Opt-out: Set `SKIP_EXTENSION_UPDATE=true` environment variable if needed for troubleshooting
- Connection: Uses `Provisioning:AdminConnectionString` from app configuration

**Observed startup logs**:
```
info: TansuCloud.Database.Hosting.ExtensionVersionHostedService[0]
      Running pre-flight extension version checks...
info: TansuCloud.Database.Services.ExtensionVersionService[0]
      Found 7 tenant database(s) to check
info: TansuCloud.Database.Services.ExtensionVersionService[0]
      [tansu_tenant_acme_dev] Updated extension vector from 0.8.0 to 0.8.1
info: TansuCloud.Database.Services.ExtensionVersionService[0]
      Pre-flight extension checks completed. Processed 7 database(s)
```

**Pros**:
- ✅ Automatic safety net - zero manual intervention required
- ✅ Prevents XX000 errors before they happen
- ✅ Works across dev/staging/production
- ✅ Observable via structured logs
- ✅ Fail-fast behavior prevents partial upgrades

**Cons**:
- Adds 5-10 seconds to startup (7 databases × ~1 second per extension)
- Requires read access to `pg_database` catalog

### 3. Blue-Green Deployment (Zero-Downtime)

For production clusters, deploy the new version alongside the old, validate, then switch traffic.

**Steps**:

1. **Build new image** with pinned versions:

   ```bash
   docker build -f dev/Dockerfile.citus-pgvector.prod \
     -t yourorg/citus-pgvector:citus13.2-pg16-pgvector0.8.0 .
   docker push yourorg/citus-pgvector:citus13.2-pg16-pgvector0.8.0
   ```

2. **Deploy green stack** (parallel to production):

   ```yaml
   # docker-compose.green.yml
   services:
     postgres-green:
       image: yourorg/citus-pgvector:citus13.2-pg16-pgvector0.8.0
       volumes:
         - postgres-green-data:/var/lib/postgresql/data
       networks:
         - green-network
   ```

3. **Copy production data** (or use read replica):

   ```bash
   pg_dump -h prod-postgres -U postgres -d tansu_tenant_acme | \
     psql -h green-postgres -U postgres -d tansu_tenant_acme
   ```

4. **Update extensions** on green:

   ```sql
   DO $$
   DECLARE
       db text;
   BEGIN
       FOR db IN SELECT datname FROM pg_database 
                 WHERE datname LIKE 'tansu_tenant_%'
       LOOP
           EXECUTE format('ALTER EXTENSION citus UPDATE') 
               USING DATABASE = db;
           RAISE NOTICE 'Updated Citus in %', db;
       END LOOP;
   END $$;
   ```

5. **Smoke test** green stack:

   ```bash
   curl -f http://green-gateway:8080/health/ready
   curl -f http://green-gateway:8080/db/health/ready
   ```

6. **Switch DNS/Load Balancer** to green:

   ```bash
   # Update DNS CNAME or LB backend pool
   aws route53 change-resource-record-sets --hosted-zone-id Z1234 \
     --change-batch file://switch-to-green.json
   ```

7. **Monitor** for 24-48 hours

8. **Decommission blue** stack:

   ```bash
   docker-compose -f docker-compose.blue.yml down
   ```

**Rollback**: Switch DNS/LB back to blue immediately if issues arise.

### 4. Migration-Based Updates

Include extension updates in Entity Framework migrations for version control.

**Create migration**:

```bash
dotnet ef migrations add UpdateCitusExtension --project TansuCloud.Database
```

**Migration code**:

```csharp
public partial class UpdateCitusExtension : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER EXTENSION citus UPDATE;");
        migrationBuilder.Sql("ALTER EXTENSION vector UPDATE;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Extensions don't support downgrade
        migrationBuilder.Sql("-- Cannot downgrade extensions");
    }
}
```

**Pros**: Version controlled, auditable, runs automatically on deployment

**Cons**: Requires EF migration for every extension upgrade; doesn't catch drift from manual provisioning

### 5. Health Checks (✅ IMPLEMENTED)

**Status**: Extension version health check has been implemented in the TansuCloud Database service.

**Implementation**: 
- `TansuCloud.Database/Hosting/ExtensionVersionHealthCheck.cs` - Health check for extension versions
- Registered in `TansuCloud.Database/Program.cs` with tag `extensions`

**How it works**:
- Queries `pg_extension` catalog for Citus and pgvector versions across all tenant databases
- Reports Healthy when all databases have matching versions
- Reports Degraded when version mismatches are detected
- Reports Unhealthy when database connection fails

**Endpoint**: 
```bash
# Check extension health specifically
curl -f https://apps.example.com/db/health/ready?tags=extensions

# Or query all health checks (includes extensions)
curl -f https://apps.example.com/db/health/ready
```

**Response example** (Healthy):
```json
{
  "status": "Healthy",
  "checks": {
    "extension-versions": {
      "status": "Healthy",
      "description": "All extensions up to date: Citus 13.2-1, pgvector 0.8.1 (7 databases)"
    }
  }
}
```

**Response example** (Degraded):
```json
{
  "status": "Degraded",
  "checks": {
    "extension-versions": {
      "status": "Degraded",
      "description": "Extension version mismatch detected in 2 databases: tansu_tenant_acme_dev (vector 0.8.0 expected 0.8.1), tansu_tenant_test (citus 13.1-1 expected 13.2-1)"
    }
  }
}
```

**Pros**:
- ✅ Continuous monitoring of extension health
- ✅ Early warning system for version drift
- ✅ Integrated with standard health check infrastructure
- ✅ Can be monitored by external systems (Kubernetes, load balancers, APM tools)

## Rollback Procedures

### When to Rollback

Trigger rollback if:

- Any `XX000` extension version errors in production logs
- Query latency P95 > 2x baseline for > 15 minutes
- Error rate > 1% for > 5 minutes
- Failed E2E tests for critical workflows (auth, storage, vector search)

### Procedure

1. **Revert to previous image**:

   ```bash
   docker tag yourorg/citus-pgvector:rollback-20251001 \
              yourorg/citus-pgvector:production
   docker-compose -f docker-compose.prod.yml up -d postgres
   ```

2. **Extension downgrade**:

   ⚠️ **Warning**: PostgreSQL extensions do NOT support downgrade. If the new version made schema changes, you must restore from backup.

   ```bash
   # Restore from last known good backup
   pg_restore -h postgres -U postgres -d tansu_tenant_acme backup-20251001.dump
   ```

3. **Restart services**:

   ```bash
   docker-compose restart pgcat db storage
   ```

4. **Verify health**:

   ```bash
   curl -f https://apps.example.com/health/ready
   curl -f https://apps.example.com/db/health/ready
   ```

5. **Post-mortem**:
   - Document what went wrong
   - Update staging test plan
   - Schedule retry after fixing issues

## Monitoring

### Key Metrics

1. **Extension version drift**:

   ```sql
   SELECT datname, extname, extversion 
   FROM pg_database 
   JOIN pg_extension ON (pg_database.oid = pg_extension.extdb)
   WHERE datname LIKE 'tansu_tenant_%';
   ```

   Export to SigNoz/Prometheus for alerting.

2. **Database error rate**:

   Monitor for `XX000` errors in Database service logs:

   ```bash
   docker logs tansu-db --tail 100 | grep "XX000"
   ```

3. **Query latency**:

   P95/P99 latency should remain within 2x of baseline after upgrades.

4. **Health endpoint status**:

   ```bash
   curl -f https://apps.example.com/db/health/ready
   ```

   Returns 200 only if extensions match expected versions.

### Alerts

Configure alerts for:

- Extension version mismatch (health check degraded)
- Database write errors > 1% for > 5 minutes
- Query latency P95 > 500ms for > 10 minutes

## FAQ

### Q: Why not use `:latest` in production?

**A**: `:latest` means "whatever was built most recently" which changes unpredictably. Pinning versions (e.g., `citus12.1-pg16`) lets you test upgrades in staging first and upgrade on your schedule, not Docker Hub's.

### Q: Can I automate extension updates in migrations?

**A**: Yes (see Strategy #4), but this only works for databases created via EF migrations. Manually provisioned databases won't be covered. Combine with pre-flight checks (Strategy #2) for full coverage.

### Q: What if a tenant database was provisioned outside migrations?

**A**: Pre-flight checks (Strategy #2) or startup health checks (Strategy #5) will catch and fix these. Or enumerate databases manually and update extensions via SQL script.

### Q: Do extensions support downgrade?

**A**: No. If you deploy a new extension version that makes breaking schema changes, rolling back requires restoring from backup. This is why thorough staging testing is critical.

### Q: How often should I upgrade Citus/pgvector?

**A**: Quarterly or when security patches are released. Always test in staging first. Minor version upgrades (e.g., 13.1 → 13.2) are usually safe; major versions (13.x → 14.x) require careful testing.

### Q: Can I skip extension updates if I'm only upgrading the container?

**A**: No. If the container's shared library changes, PostgreSQL **requires** matching extension metadata. Skipping updates causes `XX000` errors and complete service outage.

---

## Related Documentation

- [Guide-For-Admins-and-Tenants.md § 9.3](../Guide-For-Admins-and-Tenants.md#93-database-container-upgrades-and-postgresql-extension-management) — Comprehensive upgrade strategies
- [Tasks-M1.md § Task 8](../Tasks-M1.md#task-8-otel-baseline-across-services) — Vector search E2E resolution notes
- [dev/Dockerfile.citus-pgvector](../dev/Dockerfile.citus-pgvector) — Current image definition

## Changelog

| Date | Version | Changes |
|------|---------|---------|
| 2025-10-07 | 1.0 | Initial document after vector search E2E troubleshooting |

---

**Maintainers**: DevOps Team  
**Last reviewed**: 2025-10-07  
**Next review**: 2025-11-07
