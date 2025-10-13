# Eliminating Database Init Scripts

**Date:** 2025-10-13  
**Issue:** Dependency on external init scripts for database initialization  
**Solution:** Database service now handles all initialization automatically  

---

## Problem Statement

Previously, the system relied on a PostgreSQL init script (`dev/db-init/01-init.sql`) that:

- Only ran on first container initialization
- Required volume deletion to re-run
- Created tight coupling between infrastructure and application
- Made the system less resilient to container recreation

This violated the principle that services should be self-initializing and self-healing.

---

## Solution Overview

Enhanced `DatabaseMigrationHostedService` to handle all database initialization tasks:

1. **Base Extensions** - Installs citus, vector, pg_trgm in `postgres` database
2. **Template1 Extensions** - Installs vector, pg_trgm in `template1` for inheritance
3. **Identity Database** - Creates `tansu_identity` database with required extensions
4. **Audit Database** - Creates `tansu_audit` database, installs extensions, applies EF Core migrations

All initialization is **idempotent** and runs on every service startup.

---

## Changes Made

### 1. Enhanced `DatabaseMigrationHostedService`

**File:** `TansuCloud.Database/Hosting/DatabaseMigrationHostedService.cs`

Added four new initialization methods:

#### `EnsureBaseExtensionsAsync()`

- Connects to `postgres` database
- Installs: `citus`, `vector`, `pg_trgm`
- Uses `CREATE EXTENSION IF NOT EXISTS` for idempotency
- Logs warnings if extensions unavailable (graceful degradation)

#### `EnsureTemplate1ExtensionsAsync()`

- Connects to `template1` database
- Installs: `vector`, `pg_trgm` (not citus - requires per-DB setup)
- New databases automatically inherit these extensions

#### `EnsureIdentityDatabaseAsync()`

- Creates `tansu_identity` database if missing
- Installs required extensions: `vector`, `pg_trgm`
- Idempotent - safe to run multiple times

#### `EnsureAuditDatabaseAsync()` - Enhanced

- Creates `tansu_audit` database if missing
- Installs required extensions: `vector`, `pg_trgm`
- Applies EF Core migrations
- All steps idempotent

### 2. Startup Sequence

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    // Step 1: Base extensions in postgres
    await EnsureBaseExtensionsAsync(cancellationToken);
    
    // Step 2: Extensions in template1 for inheritance
    await EnsureTemplate1ExtensionsAsync(cancellationToken);
    
    // Step 3: Identity database with extensions
    await EnsureIdentityDatabaseAsync(cancellationToken);
    
    // Step 4: Audit database with extensions + migrations
    await EnsureAuditDatabaseAsync(cancellationToken);
}
```

### 3. Docker Compose Changes

**File:** `docker-compose.yml` (Line 21)

Init script mount already commented out:

```yaml
# - ./dev/db-init:/docker-entrypoint-initdb.d:ro
```

No changes needed - init scripts are no longer mounted or used.

---

## Verification

### Test: Fresh Start Without Init Scripts

```bash
# Clean everything
docker compose down -v

# Start infrastructure only (no init scripts)
docker compose up -d postgres redis pgcat

# Build and start Database service
docker compose build db
docker compose up -d db

# Wait and check logs
docker logs tansu-db --tail 50
```

### Results ✅

**Database Service Logs:**

```
info: DatabaseMigrationHostedService[0]
      Extension 'citus' ensured in postgres database.
info: DatabaseMigrationHostedService[0]
      Extension 'vector' ensured in postgres database.
info: DatabaseMigrationHostedService[0]
      Extension 'pg_trgm' ensured in postgres database.
info: DatabaseMigrationHostedService[0]
      Extension 'vector' ensured in template1.
info: DatabaseMigrationHostedService[0]
      Extension 'pg_trgm' ensured in template1.
info: DatabaseMigrationHostedService[0]
      Identity database 'tansu_identity' created successfully.
info: DatabaseMigrationHostedService[0]
      Extension 'vector' ensured in tansu_identity database.
info: DatabaseMigrationHostedService[0]
      Extension 'pg_trgm' ensured in tansu_identity database.
info: DatabaseMigrationHostedService[0]
      Audit database 'tansu_audit' created successfully.
info: DatabaseMigrationHostedService[0]
      Extension 'vector' ensured in tansu_audit database.
info: DatabaseMigrationHostedService[0]
      Extension 'pg_trgm' ensured in tansu_audit database.
info: DatabaseMigrationHostedService[0]
      Audit database migrations applied successfully.
info: DatabaseMigrationHostedService[0]
      DatabaseMigrationHostedService: All database initialization and migrations completed successfully.
```

**Database Verification:**

```bash
$ docker exec tansu-postgres psql -U postgres -c "\l" | grep tansu
tansu_audit    | postgres | UTF8 | ...
tansu_identity | postgres | UTF8 | ...

$ docker exec tansu-postgres psql -U postgres -d tansu_audit -c "\dx"
Name   | Version | Description
-------+---------+--------------------------------------
pg_trgm| 1.6     | text similarity measurement
vector | 0.8.1   | vector data type and access methods

$ docker exec tansu-postgres psql -U postgres -d tansu_identity -c "\dx"
Name   | Version | Description
-------+---------+--------------------------------------
pg_trgm| 1.6     | text similarity measurement
vector | 0.8.1   | vector data type and access methods
```

**Service Status:**

```bash
$ docker ps --filter name=tansu-db
NAMES      STATUS                        PORTS
tansu-db   Up 2 minutes (healthy)        8080/tcp
```

---

## Benefits

### 1. **Resilience**

- Service recreates databases automatically if they're deleted
- No dependency on init script execution timing
- Survives container/volume recreation

### 2. **Simplicity**

- No external init scripts to maintain
- All initialization logic in one place (Database service)
- Easier to understand and debug

### 3. **Idempotency**

- Safe to run multiple times
- Extensions use `IF NOT EXISTS`
- Database creation checks existence first
- EF Core migrations are naturally idempotent

### 4. **Self-Healing**

- Missing databases are created automatically
- Missing extensions are installed automatically
- Missing tables are created via migrations

### 5. **Development Experience**

- `docker compose up` just works
- No manual database setup steps
- Consistent behavior across environments

---

## Migration Path

### For Existing Deployments

1. **No action required** - init scripts can remain in place
2. Database service will validate and ensure everything exists
3. Init scripts become redundant but harmless

### For New Deployments

1. Start with `docker compose up -d`
2. Database service handles all initialization
3. No manual steps needed

### Removing Init Scripts (Optional)

If you want to fully remove init scripts:

1. **Dev environment:** Already commented out in `docker-compose.yml`
2. **Production:** Remove volume mount from compose file
3. **Repository:** Can archive `dev/db-init/` directory

**Note:** There's no rush to remove init scripts. They don't interfere with the new initialization logic.

---

## Comparison: Before vs After

### Before (Init Script Approach)

```yaml
# docker-compose.yml
services:
  postgres:
    volumes:
      - ./dev/db-init:/docker-entrypoint-initdb.d:ro  # Required!
```

**Limitations:**

- ❌ Only runs on first container start
- ❌ Requires volume deletion to re-run
- ❌ External dependency (init script file)
- ❌ No visibility into what happened
- ❌ Different behavior in dev vs prod

### After (Service Initialization)

```yaml
# docker-compose.yml
services:
  postgres:
    volumes:
      # No init scripts needed!
```

**Advantages:**

- ✅ Runs on every service start
- ✅ Self-healing and idempotent
- ✅ All logic in service code
- ✅ Full logging and observability
- ✅ Consistent across environments

---

## Technical Details

### Extension Installation

Extensions are installed with `CREATE EXTENSION IF NOT EXISTS`:

```sql
CREATE EXTENSION IF NOT EXISTS citus;
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
```

This ensures:

- No errors if extension already exists
- No action if already installed
- Graceful failure if extension not available

### Database Creation

Databases are checked before creation:

```csharp
await using var checkCmd = new NpgsqlCommand(
    "SELECT 1 FROM pg_database WHERE datname = 'tansu_audit'",
    conn
);
var exists = await checkCmd.ExecuteScalarAsync(ct);

if (exists == null)
{
    await using var createCmd = new NpgsqlCommand(
        "CREATE DATABASE tansu_audit",
        conn
    );
    await createCmd.ExecuteNonQueryAsync(ct);
}
```

### Error Handling

- **Extension installation failures** → Logged as warnings, service continues
- **Database creation failures** → Logged as errors, service stops (fail-fast)
- **Migration failures** → In production: stops service; In development: logs and continues

---

## Future Enhancements

### 1. Identity Database Migrations

Currently, Identity tables are created by the Identity service via EF Core. Could consolidate:

- Create EF Core context for Identity schema
- Add migrations to track schema versions
- Apply from Database service alongside Audit migrations

### 2. Extension Version Tracking

Could track extension versions alongside schema versions:

- Record installed extension versions
- Detect when extensions need updates
- Apply updates automatically or notify operators

### 3. Multi-Cluster Support

For distributed Citus setups:

- Detect coordinator vs worker nodes
- Apply appropriate initialization per node type
- Coordinate schema distribution across nodes

---

## Related Documentation

- `docs/Database-Automatic-Migration-Implementation.md` - Previous migration work
- `docs/DatabaseSchemas.md` - Database schema reference
- `Architecture.md` - Schema management architecture
- Task 43 in `Tasks-M4.md` - Database governance requirements

---

## Rollback Plan

If issues arise with the new initialization logic:

### Quick Rollback

1. Restore init scripts:

   ```yaml
   # docker-compose.yml
   volumes:
     - ./dev/db-init:/docker-entrypoint-initdb.d:ro
   ```

2. Clean and restart:

   ```bash
   docker compose down -v
   docker compose up -d
   ```

### Selective Rollback

Comment out specific initialization methods in `DatabaseMigrationHostedService.StartAsync()`:

```csharp
// await EnsureBaseExtensionsAsync(cancellationToken);  // Disable if needed
```

---

## Conclusion

The Database service is now fully self-initializing. It handles all database creation, extension installation, and schema migrations automatically on startup. This eliminates external init scripts, improves resilience, and simplifies operations.

**Key Achievement:** `docker compose up` now works from a completely clean state with no manual database setup required.
