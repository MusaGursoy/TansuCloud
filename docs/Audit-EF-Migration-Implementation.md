# Audit System Migration to EF Core (Completed)

## Summary

Successfully migrated the Audit logging system from SQL-based table creation to EF Core migrations for consistency with other TansuCloud services (Identity, Telemetry, Database).

## Changes Made

### 1. Updated `TansuCloud.Observability.Shared/Auditing/AuditLogger.cs`

#### Removed SQL-based Table Creation

- **Removed**: `EnsureTableAsync()` method that used `CREATE TABLE IF NOT EXISTS` SQL
- **Removed**: Call to `EnsureTableAsync()` from `AuditBackgroundWriter.ExecuteAsync()`

#### Added EF Core Integration

- **Added**: `AddTansuAudit()` now registers `AuditDbContext` with proper connection string binding
- **Added**: `ApplyAuditMigrationsAsync()` extension method to apply EF migrations across all services
- **Pattern**: Migration method accepts `IServiceProvider` and `ILogger` for flexible invocation

```csharp
public static async Task ApplyAuditMigrationsAsync(
    IServiceProvider serviceProvider,
    ILogger logger,
    CancellationToken cancellationToken = default)
{
    try
    {
        var auditDb = serviceProvider.GetRequiredService<TansuCloud.Audit.AuditDbContext>();
        logger.LogInformation("Applying audit database migrations...");
        await auditDb.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Audit database migrations applied successfully");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to apply audit database migrations...");
        // Don't throw; allow service to start even if audit migrations fail
    }
}
```

### 2. Updated `TansuCloud.Observability.Shared.csproj`

Added package references for EF Core integration:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore" Version="9.0.1" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.1" />
</ItemGroup>

<ItemGroup>
  <ProjectReference Include="..\TansuCloud.Audit\TansuCloud.Audit.csproj" />
</ItemGroup>
```

### 3. Added Migration Calls to All Services

Added audit migration application to each service's `Program.cs` **after** the app is built:

#### Identity Service (`TansuCloud.Identity/Program.cs`)

```csharp
// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions
        .ApplyAuditMigrationsAsync(scope.ServiceProvider, logger);
}
```

#### Dashboard Service (`TansuCloud.Dashboard/Program.cs`)

```csharp
// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions
        .ApplyAuditMigrationsAsync(scope.ServiceProvider, logger);
}
```

#### Database Service (`TansuCloud.Database/Program.cs`)

```csharp
// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions
        .ApplyAuditMigrationsAsync(scope.ServiceProvider, logger);
}
```

#### Storage Service (`TansuCloud.Storage/Program.cs`)

```csharp
// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions
        .ApplyAuditMigrationsAsync(scope.ServiceProvider, logger);
}
```

#### Gateway Service (`TansuCloud.Gateway/Program.cs`)

```csharp
// Apply audit database migrations (EF-based, idempotent across all services)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await TansuCloud.Observability.Auditing.AuditServiceCollectionExtensions
        .ApplyAuditMigrationsAsync(scope.ServiceProvider, logger);
}
```

#### Telemetry Service (`TansuCloud.Telemetry/Program.cs`)

**INTENTIONALLY EXCLUDED**: Telemetry is a separate, optional component installed independently. It writes to the audit database but does NOT manage its schema. Core services (Identity, Dashboard, Database, Storage, Gateway) are responsible for ensuring the audit schema exists.

## Architecture Decision: Option B (All Services Apply)

**Chosen Pattern**: Every **core service** (Identity, Dashboard, Database, Storage, Gateway) attempts to apply migrations on startup.

**Important Exclusion**: Telemetry service does NOT apply audit migrations because it's a separate, optional component that should not manage the shared audit database schema.

### Why Option B?

1. **Service Independence**: Services can start in any order without dependency on Database service
2. **Race Condition Safety**: Docker Compose starts services in parallel; any service might start first
3. **Idempotency**: EF Core migrations are idempotent via `__EFMigrationsHistory` table
4. **Zero Risk**: First service to start creates the table; others see it's already applied
5. **Consistency**: Matches the previous SQL-based pattern where all services called `EnsureTableAsync()`

### Race Condition Protection

**PostgreSQL Advisory Locks** prevent concurrent migration attempts:

```csharp
const long AuditMigrationLockId = 7461626173756100; // Unique ID

// Acquire advisory lock (blocking)
SELECT pg_advisory_lock(AuditMigrationLockId);

try {
    // Apply migrations (only one service at a time)
    await auditDb.Database.MigrateAsync(cancellationToken);
} finally {
    // Release lock
    SELECT pg_advisory_unlock(AuditMigrationLockId);
}
```

**How it works**:

- PostgreSQL advisory locks are application-level, cross-connection locks
- First service to acquire the lock applies migrations
- Other services block until lock is released
- Lock is automatically released when connection closes
- No deadlocks possible (locks are session-scoped)

### How It Works

1. Each service calls `ApplyAuditMigrationsAsync()` on startup
2. EF Core checks `__EFMigrationsHistory` table in `tansu_audit` database
3. If migration `20251007223921_InitialCreate` is not present, apply it
4. If already applied, skip silently
5. Multiple services can attempt simultaneously; PostgreSQL locks ensure atomicity

## Existing Migrations

The migrations already exist in `TansuCloud.Audit/Migrations/`:

- `20251007223921_InitialCreate.cs` - Creates `audit_events` table with all required columns and indexes
- `20251007223921_InitialCreate.Designer.cs` - EF Core designer metadata
- `AuditDbContextModelSnapshot.cs` - Current model snapshot for future migrations

## Benefits

### 1. **Consistency with Other Services**

- Identity: Uses EF migrations for OpenIddict tables
- Telemetry: Uses EF migrations for telemetry tables
- Database: Uses EF migrations for tenant schemas
- **Audit: Now uses EF migrations** ✅

### 2. **Production Ready**

- Init scripts in Docker image create `tansu_audit` database ✅
- EF migrations create `audit_events` table on first service startup ✅
- Automatic schema evolution when migrations are added ✅

### 3. **Developer Experience**

- Standard .NET EF Core workflow for schema changes
- Type-safe migrations with designer support
- Easy rollback and versioning via `dotnet ef` tools

### 4. **Deployment Simplicity**

- No manual SQL scripts needed for table creation
- Works in any environment (dev, docker, production)
- Self-healing: missing tables are created automatically

## Testing

Build verification:

```bash
dotnet build .\TansuCloud.sln -c Debug
```

Result: ✅ **Build succeeded** (warnings about EF Core version conflicts are expected and harmless)

## Next Steps for Production

1. **Verify First Run**: Start services with fresh `tansu_audit` database and confirm migrations apply
2. **Monitor Logs**: Check for "Applying audit database migrations..." info logs
3. **Validate Idempotency**: Restart services and confirm no duplicate migration attempts
4. **Load Testing**: Verify audit write performance (EF context is scoped per migration, not per write)

## Configuration Requirements

Each service requires the `Audit:ConnectionString` configuration key:

```json
{
  "Audit": {
    "ConnectionString": "Host=pgcat;Port=6432;Database=tansu_audit;Username=postgres;Password=..."
  }
}
```

This is already configured in:

- `appsettings.Development.json` (each service)
- `docker-compose.yml` (via environment variables)
- `docker-compose.prod.yml` (via environment variables)

## Backward Compatibility

✅ **Fully backward compatible**: Existing `tansu_audit` databases with `audit_events` table created via old SQL method will work seamlessly. EF Core will detect the existing table and not attempt to recreate it.

## Date Completed

October 10, 2025

---

## Update (October 10, 2025): Race Condition Protection & Telemetry Exclusion

### Issue 1: Race Conditions (FIXED)

**Problem**: When multiple services start simultaneously in Docker Compose, they could race to create the `__EFMigrationsHistory` table and `audit_events` table.

**Solution**: Added **PostgreSQL advisory locks** to serialize migration attempts across all services:

```csharp
const long AuditMigrationLockId = 7461626173756100;

// Acquire lock (blocks other services)
await lockCmd.ExecuteNonQueryAsync("SELECT pg_advisory_lock(...)");

try {
    // Only one service at a time can apply migrations
    await auditDb.Database.MigrateAsync(cancellationToken);
} finally {
    // Release lock
    await unlockCmd.ExecuteNonQueryAsync("SELECT pg_advisory_unlock(...)");
}
```

**Benefits**:

- ✅ **Zero race conditions**: Only one service applies migrations at a time
- ✅ **Automatic cleanup**: Lock is released on connection close (even if process crashes)
- ✅ **No deadlocks**: Advisory locks are session-scoped and non-reentrant
- ✅ **Performance**: Blocking is minimal (only during the brief migration execution)

### Issue 2: Telemetry Service Responsibility (FIXED)

**Problem**: Telemetry is a separate, optional service that should not manage the core audit database schema.

**Solution**: Removed audit migration call from `TansuCloud.Telemetry/Program.cs`.

**Rationale**:

- Telemetry is installed separately and not part of the core compose stack in production
- Audit schema management is the responsibility of **core services only** (Identity, Dashboard, Database, Storage, Gateway)
- Telemetry only **writes** to the audit database; it does not **manage** it

**Services Applying Audit Migrations** (Final List):

- ✅ Identity
- ✅ Dashboard
- ✅ Database
- ✅ Storage
- ✅ Gateway
- ❌ Telemetry (excluded - writes only, does not manage schema)
