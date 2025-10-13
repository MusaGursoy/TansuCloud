# PgCat Dynamic Tenant Pool Management - Production Considerations

**Date**: October 12, 2025  
**Status**: Auto-reload is functional, but timing window creates edge case risk

## Current Implementation

✅ **Working auto-reload system**:
```
pgcat-config (every 15s) → discovers tenant DBs → writes pgcat.toml
                                                         ↓
pgcat (auto-reload 15s) → detects file change → reloads pools
```

**Time to tenant availability**: 0-30 seconds after database creation (worst case: 15s for config write + 15s for PgCat reload)

## Production Risk: Timing Window

### Scenario
```
T+0s:  Admin API provisions tenant "acme-corp"
T+0s:  Postgres creates tansu_tenant_acme_corp
T+0s:  API returns 200 OK to admin
T+1s:  Customer tries to access tenant
T+1s:  ❌ FAIL: "No pool configured for database: tansu_tenant_acme_corp"
...
T+30s: pgcat-config discovers database + PgCat reloads
T+31s: ✅ Customer retry succeeds
```

**Impact**: 0-30 second window where provisioned tenants are inaccessible

## Recommended Solutions

### Option 1: Synchronous Pool Creation (Recommended for Production)

**Approach**: Database service directly triggers PgCat pool creation via PgCat Admin API

**Implementation**:
```csharp
// In TansuCloud.Database provisioning endpoint
public async Task<IActionResult> ProvisionTenant(string tenantId)
{
    // 1. Create database in Postgres
    await CreateTenantDatabase(tenantId);
    
    // 2. Trigger PgCat pool creation immediately (via Admin API)
    await AddPgCatPool(tenantId);
    
    // 3. Return success only after pool is ready
    return Ok(new { created = true, tenantId });
}
```

**PgCat Admin API** (documented at https://postgresml.org/docs/resources/pooler/administration):
- POST `/pools/{pool_name}` - Add new pool dynamically
- Instant availability (no waiting for auto-reload)
- Retry-safe (idempotent)

**Benefits**:
- ✅ Zero downtime for new tenants
- ✅ Provisioning returns only when tenant is fully ready
- ✅ No race conditions
- ✅ Keeps auto-reload as fallback safety net

**Drawbacks**:
- Requires PgCat Admin API integration
- Adds dependency on PgCat availability during provisioning

---

### Option 2: Retry Logic in Database Service (Current Mitigation)

**Approach**: Database service retries "No pool configured" errors with exponential backoff

**Implementation**:
```csharp
public async Task<DbConnection> GetTenantConnection(string tenantId)
{
    var retries = 0;
    while (retries < 5)
    {
        try
        {
            return await dataSource.OpenConnectionAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "58000" && 
                                           ex.Message.Contains("No pool configured"))
        {
            retries++;
            await Task.Delay(TimeSpan.FromSeconds(5 * retries)); // 5s, 10s, 15s, 20s, 25s
        }
    }
    throw;
}
```

**Benefits**:
- ✅ Simple to implement
- ✅ No external dependencies
- ✅ Handles auto-reload timing gracefully

**Drawbacks**:
- Adds latency to first request after provisioning
- Doesn't fix the root cause

---

### Option 3: Reduce Auto-Reload Interval (Quick Win)

**Approach**: Decrease polling/reload intervals to 5 seconds

**docker-compose.yml**:
```yaml
pgcat-config:
  environment:
    POLL_INTERVAL_SECONDS: 5  # Add to Program.cs as env var
```

**Program.cs**:
```csharp
var pollSeconds = int.Parse(Environment.GetEnvironmentVariable("POLL_INTERVAL_SECONDS") ?? "15");
await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cts.Token);
```

**Benefits**:
- ✅ Reduces timing window from 30s → 10s worst case
- ✅ No architectural changes
- ✅ Easy to implement

**Drawbacks**:
- Slightly higher resource usage (negligible for lightweight poller)
- Doesn't eliminate the race condition

---

### Option 4: Pre-provision Known Tenants (CI/Test Environments)

**Approach**: For test/staging environments, provision known tenant databases at startup

**Implementation**: Add to init scripts or startup task:
```sql
-- dev/db-init/02-test-tenants.sql
CREATE DATABASE IF NOT EXISTS tansu_tenant_e2e_server_ank;
CREATE DATABASE IF NOT EXISTS tansu_tenant_nightly_server_ank;
```

**Benefits**:
- ✅ Eliminates test failures
- ✅ Faster test execution (no waiting for auto-reload)

**Drawbacks**:
- Only helps non-production environments
- Requires maintenance of tenant list

---

## Recommended Production Strategy

**Layered approach** for maximum reliability:

1. **Primary**: Option 1 - Synchronous pool creation via PgCat Admin API
   - Guarantees instant tenant availability
   - Production-grade reliability

2. **Fallback**: Keep auto-reload (current implementation)
   - Handles edge cases (PgCat restart, manual database creation)
   - Catch-all safety net

3. **Monitoring**: Add metrics/alerts
   - Track "No pool configured" errors
   - Alert if auto-reload stops working
   - Dashboard for tenant pool status

4. **Documentation**: Update admin guide
   - Document 30-second SLA for tenant availability (current)
   - Or document instant availability (after Option 1)

## Implementation Priority

### Phase 1 (Immediate - Low Risk Mitigation)
- [x] Verify auto-reload is working (DONE - it is!)
- [ ] Add retry logic in Database service (Option 2)
- [ ] Reduce poll interval to 5 seconds (Option 3)
- [ ] Update `Guide-For-Admins-and-Tenants.md` with 30s SLA

### Phase 2 (Production Hardening)
- [ ] Integrate PgCat Admin API for synchronous pool creation (Option 1)
- [ ] Add Prometheus metrics for pool operations
- [ ] Add SigNoz traces for tenant provisioning flow
- [ ] Create runbook for "tenant not accessible" troubleshooting

### Phase 3 (Operational Excellence)
- [ ] Automated testing of tenant provisioning → immediate access
- [ ] Chaos engineering: simulate PgCat restart during provisioning
- [ ] Consider alternative connection poolers (PgBouncer, Odyssey) if PgCat limitations persist

## Current Risk Level: **MEDIUM** ⚠️

**Without immediate action**: Newly provisioned tenants have 0-30 second downtime window

**With Phase 1 complete**: Risk reduced to **LOW** - acceptable for most production workloads

**With Phase 2 complete**: Risk mitigated to **MINIMAL** - enterprise-grade reliability

## Next Steps

1. Implement Option 2 (retry logic) and Option 3 (faster polling) immediately
2. Test tenant provisioning → immediate access flow
3. Schedule Option 1 (Admin API integration) for next sprint
4. Update production deployment docs with current behavior and SLA

---

## References

- PgCat Admin API: https://postgresml.org/docs/resources/pooler/administration
- PgCat Configuration: https://github.com/postgresml/pgcat
- Current implementation: `Dev.PgcatConfigurator/Program.cs`
- Docker compose: `docker-compose.yml` (pgcat-config service)
