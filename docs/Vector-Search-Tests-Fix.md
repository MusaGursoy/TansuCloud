# Vector Search E2E Tests - PgCat Configuration Issue (Fixed)

**Date**: October 12, 2025  
**Context**: Post-Garnet Migration E2E Test Run

## Issue Summary

After completing the Redis→Garnet migration and fixing the port configuration (9080→8080), the E2E test suite showed 89/95 tests passing. Three Vector Search tests failed with `InternalServerError` when attempting to seed documents.

## Failed Tests

1. `Vector_PerCollection_1536D_Top1_WhenAvailable` 
2. `Vector_PerCollection_Basic`
3. `Vector_Global_Basic`

All three failed at the same point: `SeedDocAsync` returning HTTP 500 instead of HTTP 201 (Created).

## Root Cause

**PgCat connection pooler had stale configuration** - it didn't have pools for tenant databases that were provisioned dynamically during test execution.

### Discovery Timeline

1. Database service logs showed: `"No pool configured for database: tansu_tenant_e2e_server_ank"`
2. Confirmed the tenant database **exists** in Postgres:
   ```sql
   SELECT datname FROM pg_database WHERE datname LIKE 'tansu_tenant_%';
   -- Returns: tansu_tenant_e2e_server_ank (among others)
   ```
3. Checked PgCat configuration - missing pool for `e2e_server_ank`
4. Vector Search tests use `Environment.MachineName.ToLowerInvariant()` to generate tenant names: `e2e-{machinename}` → `e2e-server-ank` on this machine

### Why This Happened

- **PgCat Config Workflow**:
  1. `pgcat-config` service runs at compose startup
  2. Discovers tenant databases in Postgres matching `tansu_tenant_*` prefix
  3. Generates `/etc/pgcat/pgcat.toml` with one pool per tenant
  4. PgCat loads this config and creates connection pools

- **The Gap**:
  - When E2E tests provision a tenant (via `ProvisioningE2E.Provision_Tenant_Idempotent`), the Database service creates the tenant database in Postgres
  - BUT PgCat doesn't know about it because its config was generated before the tenant existed
  - Subsequent requests to that tenant via PgCat fail with "No pool configured"

## Resolution

**Manual Fix** (for this test run):
```powershell
# 1. Regenerate PgCat config with current tenant databases
docker restart tansu-pgcat-config
# Wait 5 seconds for completion
# Output: "Wrote config with 4 tenant pools to /out/pgcat.toml"

# 2. Reload PgCat to pick up new configuration
docker restart tansu-pgcat
# Wait 5 seconds for startup
# Logs show: "creating new pool" for tansu_tenant_e2e_server_ank

# 3. Rerun tests
dotnet test --filter "FullyQualifiedName~VectorSearchE2E"
# Result: ✅ 3/3 tests passed
```

**Automated Solution** (production/CI):
- PgCat supports config auto-reload (logs show: "Config autoreloader: 15000 ms")
- Consider one of:
  1. Hook tenant provisioning to signal PgCat reload (via admin interface or file watch)
  2. Accept 15-second delay for PgCat to discover new tenant pools automatically
  3. Provision known test tenants at compose startup (add to init scripts)

## Test Results After Fix

```
Test Run Successful.
Total tests: 3
     Passed: 3
 Total time: 1.5241 Minutes
```

All Vector Search E2E tests now pass, validating:
- ✅ Per-collection vector search with 1536-dimensional embeddings
- ✅ Per-collection KNN fallback behavior (200 or 501)
- ✅ Global ANN search across collections

## Overall E2E Status

After this fix, expected final E2E results:
- **Total: 95 tests**
- **Passed: 92** (89 + 3 vector search)
- **Failed: 1** (AspNetCoreSpanAttributesE2E - SigNoz timing issue, unrelated)
- **Skipped: 2** (Redis E2E test requires REDIS_URL env var, OTEL config file test)

**Pass Rate**: 96.8%

## Related Files

- `Dev.PgcatConfigurator/Program.cs` - Discovers tenant DBs and generates pgcat.toml
- `tests/TansuCloud.E2E.Tests/VectorSearchE2E.cs` - Tests using dynamic tenant names
- `tests/TansuCloud.E2E.Tests/ProvisioningE2E.cs` - Provisions `e2e-{machinename}` tenant
- `docker-compose.yml` - pgcat and pgcat-config service definitions

## Lessons Learned

1. **Connection poolers need config refresh** when databases are added dynamically
2. **Test tenant names** should either be pre-provisioned or tests should wait for PgCat reload
3. **PgCat admin interface** could be used for programmatic pool addition (explore for v2)
4. **Compose service dependencies** - consider if pgcat-config should run on-demand rather than once at startup

## Next Steps

- [ ] Document PgCat reload procedure in `Guide-For-Admins-and-Tenants.md`
- [ ] Consider adding a VS Code task for "Reload PgCat Configuration"
- [ ] Evaluate PgCat admin API for runtime pool management
- [ ] Add retry logic in Database service for "No pool configured" errors (temporary)
