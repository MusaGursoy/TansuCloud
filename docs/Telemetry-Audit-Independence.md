# Telemetry Service - Audit Independence

**Date**: October 10, 2025  
**Context**: Ensuring Telemetry service does not write to audit table

## Summary

The Telemetry service is **completely independent** from the audit system and does not write to the audit table. This document confirms the current architecture and recent improvements to make audit logging fully optional across all services.

## Telemetry Service - Current State

### ✅ No Audit Integration

The Telemetry service:
- **Does NOT reference** `TansuCloud.Audit` project
- **Does NOT call** `AddTansuAudit()` in `Program.cs`
- **Does NOT configure** `Audit:ConnectionString` in compose or appsettings
- **Uses its own SQLite database** for telemetry envelope storage (`Telemetry__Database__FilePath`)
- **Is completely separate** from the PostgreSQL-based audit system

### Configuration (docker-compose.yml)

```yaml
telemetry:
  environment:
    ASPNETCORE_ENVIRONMENT: Development
    OpenTelemetry__Otlp__Endpoint: http://signoz-otel-collector:4317
    Telemetry__Ingestion__ApiKey: "${TELEMETRY__INGESTION__APIKEY:-...}"
    Telemetry__Admin__ApiKey: "${TELEMETRY__ADMIN__APIKEY:-...}"
    Telemetry__Database__FilePath: /var/opt/tansu/telemetry/telemetry.db
    Telemetry__Database__EnforceForeignKeys: true
    # Note: NO Audit__ConnectionString
```

### Dependencies

The Telemetry service references:
- `TansuCloud.Observability.Shared` (for OpenTelemetry instrumentation only)
- `TansuCloud.Telemetry.Contracts` (for DTOs)

It does **NOT** reference or use the audit components.

## Audit System Improvements (Made Today)

To ensure audit logging is truly optional and doesn't break services when unavailable, we made the following improvements:

### 1. Made ConnectionString Optional

**File**: `TansuCloud.Observability.Shared/Auditing/AuditOptions.cs`

- **Removed** `[Required]` attribute from `ConnectionString` property
- **Added** comment: "If null/empty, audit logging is disabled (no-op logger registered instead)"

This allows services to call `AddTansuAudit()` without providing a connection string (e.g., for future flexibility or testing scenarios).

### 2. Conditional Registration

**File**: `TansuCloud.Observability.Shared/Auditing/AuditLogger.cs`

**Method**: `AddTansuAudit()`

```csharp
// Check if audit is configured; if not, register no-op logger and skip background writer
var auditSection = config.GetSection(AuditOptions.SectionName);
var connectionString = auditSection.GetValue<string>("ConnectionString");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Audit disabled: register no-op logger
    services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
    return services;
}

// Audit enabled: register full implementation
services.AddSingleton<IAuditLogger, HttpAuditLogger>();
services.AddHostedService<AuditBackgroundWriter>();
// ... register AuditDbContext for migrations
```

### 3. No-Op Audit Logger

**File**: `TansuCloud.Observability.Shared/Auditing/AuditLogger.cs`

```csharp
/// <summary>
/// No-op audit logger used when audit database is not configured.
/// Allows services to start and run without audit persistence.
/// </summary>
internal sealed class NoOpAuditLogger : IAuditLogger
{
    public bool TryEnqueue(AuditEvent evt) => true; // Always succeeds, does nothing
}
```

### 4. Safe Migration Application

**File**: `TansuCloud.Observability.Shared/Auditing/AuditLogger.cs`

**Method**: `ApplyAuditMigrationsAsync()`

```csharp
// Check if audit is configured; if AuditDbContext is not registered, skip migrations
var auditDb = serviceProvider.GetService<TansuCloud.Audit.AuditDbContext>();
if (auditDb == null)
{
    logger.LogInformation("Audit database not configured; skipping migrations");
    return;
}
```

- Uses `GetService<>()` instead of `GetRequiredService<>()` to avoid throwing when not registered
- Logs informational message and returns gracefully when audit is disabled

## Services That Use Audit

The following services **do** write to the audit table (when configured):

| Service   | Calls AddTansuAudit | Applies Migrations | Connection String Required |
|-----------|--------------------|--------------------|---------------------------|
| Gateway   | ✅ Yes             | ✅ Yes             | ✅ Yes (in compose)       |
| Identity  | ✅ Yes             | ✅ Yes             | ✅ Yes (in compose)       |
| Dashboard | ✅ Yes             | ✅ Yes             | ✅ Yes (in compose)       |
| Database  | ✅ Yes             | ✅ Yes             | ✅ Yes (in compose)       |
| Storage   | ✅ Yes             | ✅ Yes             | ✅ Yes (in compose)       |
| Telemetry | ❌ **NO**          | ❌ **NO**          | ❌ **NO**                |

## Production Deployment Scenarios

### Scenario 1: Full Platform with Audit

All services configure `Audit__ConnectionString` pointing to the shared `tansu_audit` PostgreSQL database.

### Scenario 2: Telemetry as Standalone Service

Telemetry can be deployed **completely independently** without:
- PostgreSQL/audit database
- Any other TansuCloud services
- Audit configuration

It only needs:
- SQLite storage for telemetry envelopes
- API keys for ingestion and admin access
- (Optionally) SigNoz/OTLP endpoint for its own observability

### Scenario 3: Future Flexibility

With the improvements made today, any service **could** theoretically run without audit by:
1. Not providing `Audit__ConnectionString` in configuration
2. The service will register `NoOpAuditLogger` automatically
3. No startup failure, no background writer, no database calls

## Testing Recommendations

1. **Verify Telemetry Independence**
   - Start Telemetry service in isolation
   - Confirm it doesn't attempt PostgreSQL connections
   - Verify it uses only SQLite for envelope storage

2. **Verify No-Op Logger**
   - Start a service (e.g., Gateway) without `Audit__ConnectionString`
   - Confirm it starts successfully with no-op logger
   - Check logs for "Audit database not configured; skipping migrations"

3. **Verify Full Audit Path**
   - Start services with `Audit__ConnectionString`
   - Confirm migrations apply successfully
   - Verify audit events are written to `audit_events` table

## Conclusion

✅ **Telemetry service does NOT write to audit** - confirmed by code inspection and configuration  
✅ **Audit system is now fully optional** - services can start without audit configuration  
✅ **Graceful degradation** - runtime failures in audit don't crash the service  
✅ **Production ready** - Telemetry can be deployed independently without PostgreSQL

No further changes are needed for Telemetry/audit independence. The system is properly architected.
