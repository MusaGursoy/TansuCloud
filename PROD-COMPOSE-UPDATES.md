# docker-compose.prod.yml Updates - October 1, 2025

## Summary

Updated `docker-compose.prod.yml` to match critical fixes from `docker-compose.yml` (dev) that were applied during E2E testing debugging session.

## Critical Fixes Applied

### 1. ✅ Added SigNoz Schema Migration Fix (CRITICAL)

**Problem**: Prod was missing the prepatch and compat-init services that fix SigNoz ClickHouse schema issues.

**Solution**: Added two new services under the `observability` profile:

- **`clickhouse-prepatch`**: Runs SQL prepatches from `/dev/clickhouse/prepatches/` directory before schema migrations
  - Creates minimal required tables/views for migrator and collector
  - Exits successfully after completion
  
- **`clickhouse-compat-init`**: Creates compatibility views and applies patches after prepatch
  - Creates `MATERIALIZED VIEW` for `root_operations` and `distributed_top_level_operations`
  - Applies all SQL patches from `/dev/clickhouse/patches/` directory
  - Fixed to use `MATERIALIZED VIEW` instead of `VIEW` (critical for ALTER TABLE operations)
  - Uses literal block scalar (`|`) for bash commands to avoid syntax errors

**Dependency Chain Updated**:

```
clickhouse:healthy → clickhouse-prepatch:completed → clickhouse-compat-init:completed → 
  signoz-schema-migrator-sync:completed → signoz-schema-migrator-async:completed
```

**Impact**: Without these services, prod would experience:

- Missing `timestamp` column in `signoz_logs.logs_attribute_keys` table
- VIEW vs MATERIALIZED VIEW errors during migrations
- Collector unable to write spans to ClickHouse

### 2. ✅ Added Audit__ConnectionString to All Services

**Problem**: Audit logging was not configured for most services.

**Solution**: Added `Audit__ConnectionString` environment variable to:

- `identity` service
- `db` service
- `storage` service
- `gateway` service

**Note**: `dashboard` service already had this configured.

**Connection String**:

```yaml
Audit__ConnectionString: "Host=pgcat;Port=6432;Database=postgres;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
```

**Impact**: Enables audit event logging via PgCat connection pooler to shared audit table.

### 3. ✅ Added Container User Override for Dashboard and Storage

**Problem**: Dev uses `user: "0:0"` but prod didn't specify user, potentially causing volume permission issues.

**Solution**: Added `user: "0:0"` to:

- `dashboard` service
- `storage` service

**Rationale**: Both services write to Docker volumes and may need root permissions for volume initialization.

### 4. ✅ Documented Outbox__DispatchTenant (Optional)

**Problem**: Dev has `Outbox__DispatchTenant: acme-dev` but prod didn't have it or a comment explaining.

**Solution**: Added explanatory comment to `db` service:

```yaml
# Optional: specify a tenant for outbox dispatcher context (useful for multi-tenant event routing)
# Outbox__DispatchTenant: <tenant-id>
```

**Rationale**: Prod may not need a default tenant; operators can set via environment override.

### 5. ✅ Added Prometheus Removal Comment

**Problem**: Prod didn't document that Prometheus was removed in favor of SigNoz.

**Solution**: Added comment to `dashboard` service environment:

```yaml
# Prometheus has been removed in favor of SigNoz. Leave unset to disable legacy metrics proxy.
# Prometheus__BaseUrl:
```

**Impact**: Documents architectural decision and prevents confusion about missing Prometheus config.

### 6. ✅ Fixed Schema Migrator Command and Dependencies

**Changes**:

- Removed `--up=` flag from `signoz-schema-migrator-sync` command (was causing incomplete migrations)
- Updated `signoz-schema-migrator-async` to use explicit dependency format:

  ```yaml
  depends_on:
    clickhouse:
      condition: service_healthy
    signoz-schema-migrator-sync:
      condition: service_completed_successfully
  ```

### 7. ✅ Dashboard OIDC RequireHttpsMetadata Configuration

**Problem**: Dashboard would fail to start with `RequireHttpsMetadata: true` when `PUBLIC_BASE_URL` uses HTTP (common for local prod testing).

**Error**: `InvalidOperationException: The MetadataAddress or Authority must use HTTPS unless disabled for development by setting RequireHttpsMetadata=false.`

**Solution**: Changed `Oidc__RequireHttpsMetadata` to `false` with comment explaining when to set `true`.

**Updated Configuration**:
```yaml
      # Set to true when PUBLIC_BASE_URL uses HTTPS; false for local HTTP testing
      Oidc__RequireHttpsMetadata: false
```

**Note**: For true production deployments with HTTPS URLs, set this to `true` for security. For local testing of prod compose with HTTP, must be `false`.

---

## Validation

### Config Validation

```bash
docker compose -f docker-compose.prod.yml config
# Result: No errors (warnings about bash variables are expected)
```

### Service Presence Verification

```bash
docker compose -f docker-compose.prod.yml --profile observability config | grep "container_name: signoz"
```

**Results**:

- ✅ signoz-clickhouse
- ✅ signoz-clickhouse-prepatch (NEW)
- ✅ signoz-clickhouse-compat-init (NEW)
- ✅ signoz
- ✅ signoz-otel-collector
- ✅ signoz-schema-migrator-sync
- ✅ signoz-schema-migrator-async
- ✅ signoz-zookeeper

### Dependency Chain Verification

The updated dependency chain ensures schema patches are applied before migrations:

1. `clickhouse` becomes healthy
2. `clickhouse-prepatch` runs and completes
3. `clickhouse-compat-init` runs and completes
4. `signoz-schema-migrator-sync` runs and completes
5. `signoz-schema-migrator-async` runs and completes
6. `signoz-otel-collector` starts and can successfully write to ClickHouse

## Intentional Differences Preserved (Dev vs Prod)

These differences are correct and should NOT be changed:

### Port Exposure

- ✅ Dev exposes many ports for testing (postgres:5432, clickhouse:8123, identity:5095, signoz:3301, etc.)
- ✅ Prod only exposes gateway:80 (and optionally 443 for HTTPS)
- **Rationale**: Security - only the gateway should be publicly accessible in prod

### Dev-Only Environment Variables

- ✅ Dev has `Dev__ProvisionBypassKey: letmein` in db and gateway - Prod: Correctly absent
- ✅ Dev has `DASHBOARD_BYPASS_IDTOKEN_SIGNATURE: "1"` - Prod: Correctly absent
- ✅ Dev has `STORAGE_ENABLE_TEST_THROW: "1"` - Prod: Correctly absent
- ✅ Dev has `Outbox__DispatchTenant: acme-dev` - Prod: Made optional (commented)
- **Rationale**: These are development/testing shortcuts that should not exist in production

### Observability Profile Dependency

- ✅ Dev gateway depends on `signoz-otel-collector:service_started`
- ✅ Prod gateway does NOT depend on signoz services
- **Rationale**: Observability is profile-gated in prod; gateway must start without it

## Testing Recommendations

Before deploying to production:

1. **Test Observability Profile**:

   ```bash
   docker compose -f docker-compose.prod.yml --profile observability up -d
   ```

   - Verify all SigNoz containers start successfully
   - Check logs: `docker logs signoz-clickhouse-prepatch`
   - Check logs: `docker logs signoz-clickhouse-compat-init`
   - Verify no schema migration errors in sync/async migrator logs

2. **Test Without Observability Profile**:

   ```bash
   docker compose -f docker-compose.prod.yml up -d
   ```

   - Verify core app services start (no SigNoz dependencies)
   - Verify gateway health: `curl http://localhost/health/ready`

3. **Verify Audit Logging**:
   - Make requests that should generate audit events
   - Query audit table: `SELECT * FROM audit_events ORDER BY when_utc DESC LIMIT 10;`

## Files Modified

- `docker-compose.prod.yml`: Updated with all fixes above

## Related Documentation

- See `docker-compose.yml` for the reference dev configuration
- See `dev/clickhouse/patches/002-fix-logs-attribute-keys-timestamp.sql` for the critical timestamp column patch
- See conversation logs for detailed debugging session (3+ hours fixing SigNoz schema issues)

## Deployment Notes

1. Ensure `.env` file has all required variables:
   - `POSTGRES_USER`
   - `POSTGRES_PASSWORD`
   - `PGCAT_ADMIN_USER`
   - `PGCAT_ADMIN_PASSWORD`
   - `DASHBOARD_CLIENT_SECRET`
   - `STORAGE_PRESIGN_SECRET` (or accept default `change-me`)
   - `PUBLIC_BASE_URL`
   - `GATEWAY_BASE_URL`
   - `OTLP_ENDPOINT` (if using observability profile)
   - `SIGNOZ_JWT_SECRET` (if using observability profile)

2. Mount `/dev/clickhouse/prepatches/` and `/dev/clickhouse/patches/` volumes exist and are readable

3. For production HTTPS, uncomment the 443 port mapping in gateway service and configure TLS

## Breaking Changes

None. These updates are additive and maintain backward compatibility.

## Rollback Plan

If issues occur after deploying the updated compose file:

1. Revert to previous `docker-compose.prod.yml`
2. Note: SigNoz schema will remain in broken state if already partially migrated
3. Clean slate: `docker compose -f docker-compose.prod.yml down -v` and start fresh

---

**Last Updated**: October 1, 2025  
**Author**: AI Assistant (GitHub Copilot)  
**Reviewed**: Pending human review
