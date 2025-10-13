# Telemetry Service - Production Resilience Configuration

**Date**: 2025-10-10  
**Feature**: Optional OTLP export disabling for [`TansuCloud.Telemetry`](TansuCloud.Telemetry ) service

## Overview

The [`TansuCloud.Telemetry`](TansuCloud.Telemetry ) service is designed to ingest logs from all other TansuCloud services. To avoid circular dependencies and ensure graceful operation when SigNoz is unavailable, we've added the ability to **disable OTLP export** for the Telemetry service in production while keeping it enabled for development and for all other services.

## Problem Statement

**Without this feature:**
- Telemetry service ingests logs from Gateway, Identity, Dashboard, Database, and Storage
- If Telemetry also tries to send its own telemetry to SigNoz via OTLP, and SigNoz is down or unreachable:
  - Potential circular dependency (Telemetry → SigNoz → Telemetry)
  - OTLP exporter retry loops consuming resources
  - Operational complexity in debugging
  - Risk of Telemetry service degrading if it can't reach SigNoz

**With this feature:**
- Telemetry service can be configured to skip OTLP export entirely in production
- Service continues accepting and storing logs from other services regardless of SigNoz availability
- Cleaner separation of concerns: Telemetry is self-contained, other services report to SigNoz
- Production resilience: Telemetry never blocks or degrades due to observability backend issues

## Implementation

### 1. Configuration Schema

Added `OpenTelemetry:Otlp:Enabled` flag to control OTLP export per service.

**Development Configuration** (`TansuCloud.Telemetry/appsettings.Development.json`):
```json
{
  "OpenTelemetry": {
    "Otlp": {
      "Enabled": true,
      "Endpoint": "http://127.0.0.1:4317"
    }
  }
}
```

**Production Configuration** (`TansuCloud.Telemetry/appsettings.json`):
```json
{
  "OpenTelemetry": {
    "Otlp": {
      "Enabled": false,
      "Endpoint": "http://signoz-otel-collector:4317"
    }
  }
}
```

### 2. Code Changes

**`TansuCloud.Observability.Shared/OpenTelemetryExtensions.cs`**:

Added check for `Enabled` flag before configuring OTLP exporter:

```csharp
public static OpenTelemetryBuilder AddTansuOtlpExporter(
    this OpenTelemetryBuilder builder, 
    IConfiguration configuration)
{
    var otlpEnabled = configuration.GetValue<bool>("OpenTelemetry:Otlp:Enabled", defaultValue: true);
    if (!otlpEnabled)
    {
        // OTLP export disabled via configuration; skip exporter setup
        return builder;
    }

    var otlpEndpoint = configuration["OpenTelemetry:Otlp:Endpoint"] 
        ?? "http://signoz-otel-collector:4317";

    return builder.UseOtlpExporter(
        OtlpExportProtocol.Grpc,
        new Uri(otlpEndpoint),
        configureBatch: batch =>
        {
            batch.MaxQueueSize = 2048;
            batch.ScheduledDelayMilliseconds = 5000;
            batch.ExporterTimeoutMilliseconds = isDevelopment ? 10000 : 30000;
            batch.MaxExportBatchSize = 512;
        },
        configureRetry: retry =>
        {
            retry.InitialRetryDelayMilliseconds = 1000;
            retry.MaxRetryDelayMilliseconds = 16000;
            retry.MaxRetryAttempts = 5;
        }
    );
}
```

### 3. Environment Variable Override

Can be overridden via environment variable for flexibility:

```bash
# Enable OTLP export (override production default)
OpenTelemetry__Otlp__Enabled=true

# Disable OTLP export (override development default)
OpenTelemetry__Otlp__Enabled=false
```

### 4. Docker Compose Configuration

**Development** (`docker-compose.yml`):
```yaml
telemetry:
  build:
    context: .
    dockerfile: TansuCloud.Telemetry/Dockerfile
  environment:
    - ASPNETCORE_ENVIRONMENT=Development
    - OpenTelemetry__Otlp__Enabled=true  # Explicit for clarity
    - OpenTelemetry__Otlp__Endpoint=http://signoz-otel-collector:4317
```

**Production** (`docker-compose.prod.yml`):
```yaml
telemetry:
  build:
    context: .
    dockerfile: TansuCloud.Telemetry/Dockerfile
  environment:
    - ASPNETCORE_ENVIRONMENT=Production
    - OpenTelemetry__Otlp__Enabled=false  # Disable OTLP to avoid circular dependencies
    - OpenTelemetry__Otlp__Endpoint=http://signoz-otel-collector:4317
```

## Behavior Matrix

| Service | Environment | OTLP Enabled | Behavior |
|---------|-------------|--------------|----------|
| Gateway | Development | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Gateway | Production | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Identity | Development | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Identity | Production | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Dashboard | Development | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Dashboard | Production | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Database | Development | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Database | Production | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Storage | Development | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| Storage | Production | ✅ Yes | Sends traces/metrics/logs to SigNoz |
| **Telemetry** | **Development** | ✅ **Yes** | **Sends traces/metrics/logs to SigNoz (for debugging)** |
| **Telemetry** | **Production** | ❌ **No** | **Self-contained; stores logs locally only** |

## Benefits

### 1. Production Resilience
- Telemetry service continues operating normally even if SigNoz is completely unavailable
- No OTLP retry loops consuming resources
- No risk of Telemetry service degrading due to observability backend issues

### 2. Circular Dependency Prevention
- Clean separation: Telemetry ingests logs, other services report to SigNoz
- Avoids potential feedback loops (Telemetry → SigNoz → Telemetry)
- Simpler debugging when observability stack has issues

### 3. Resource Efficiency
- No wasted CPU/memory on OTLP export attempts when SigNoz is down
- Reduced network traffic in production
- Telemetry database remains authoritative source for ingested logs

### 4. Flexibility
- Can still enable OTLP for Telemetry in production if needed (via env var)
- Development keeps OTLP enabled for full observability during debugging
- Per-service control allows different configurations for different deployment scenarios

## Operational Guidance

### When to Keep OTLP Enabled for Telemetry

✅ **Enable in these scenarios:**
- Development and staging environments (for full observability)
- Troubleshooting production issues (temporarily enable via env var)
- When SigNoz is highly available with guaranteed uptime
- When you want Telemetry's own metrics in SigNoz for analysis

### When to Disable OTLP for Telemetry

✅ **Disable in these scenarios:**
- Production environments with SigNoz that may be temporarily unavailable
- When you want Telemetry to be fully self-contained
- To avoid circular dependencies in observability stack
- When network bandwidth to SigNoz is limited
- During SigNoz maintenance windows

### How to Toggle in Production

**To disable OTLP export (recommended default):**
```bash
# In docker-compose.prod.yml or .env
OpenTelemetry__Otlp__Enabled=false
```

**To enable OTLP export (if needed for debugging):**
```bash
# Override via environment variable
docker exec -it tansu-telemetry \
  bash -c 'export OpenTelemetry__Otlp__Enabled=true && dotnet TansuCloud.Telemetry.dll'

# Or update docker-compose.prod.yml and restart
OpenTelemetry__Otlp__Enabled=true
docker compose -f docker-compose.prod.yml restart telemetry
```

### Monitoring Telemetry Service Health

Even with OTLP disabled, monitor Telemetry via:

1. **Health endpoints**: `/health/live` and `/health/ready` (always enabled)
2. **Container logs**: `docker logs tansu-telemetry`
3. **Database queries**: Check `telemetry_logs` table for ingestion rates
4. **Application metrics**: CPU/memory via Docker stats

## Testing

### Verify OTLP is Disabled in Production

```bash
# 1. Start production stack
docker compose -f docker-compose.prod.yml up -d

# 2. Check Telemetry environment variables
docker exec tansu-telemetry printenv | grep OpenTelemetry

# Expected output:
# OpenTelemetry__Otlp__Enabled=false
# OpenTelemetry__Otlp__Endpoint=http://signoz-otel-collector:4317

# 3. Check logs for OTLP messages (should NOT see OTLP export attempts)
docker logs tansu-telemetry 2>&1 | grep -i "otlp\|exporter"

# 4. Verify service is healthy
curl http://127.0.0.1:8080/telemetry/health/ready
```

### Verify OTLP is Enabled in Development

```bash
# 1. Start dev stack
docker compose up -d

# 2. Check Telemetry environment variables
docker exec tansu-telemetry printenv | grep OpenTelemetry

# Expected output:
# OpenTelemetry__Otlp__Enabled=true
# OpenTelemetry__Otlp__Endpoint=http://signoz-otel-collector:4317

# 3. Check logs for OTLP export success
docker logs tansu-telemetry 2>&1 | grep -i "otlp\|exporter"

# 4. Verify traces appear in SigNoz
# Navigate to http://127.0.0.1:3301 and check for "tansu.cloud.telemetry" service
```

## Fallback Behavior

If `OpenTelemetry:Otlp:Enabled` is not explicitly set:
- **Default**: `true` (OTLP export enabled)
- This ensures backward compatibility with existing deployments
- Production deployments should explicitly set it to `false` in [`TansuCloud.Telemetry/appsettings.json`](TansuCloud.Telemetry/appsettings.json )

## Future Enhancements

Potential improvements outside current scope:

1. **Dynamic toggling**: Add admin API endpoint to enable/disable OTLP at runtime
2. **Conditional export**: Only export Telemetry errors/warnings to SigNoz, skip info/debug
3. **Buffered mode**: Queue Telemetry's own metrics locally and flush when SigNoz comes back online
4. **Health check integration**: Automatically disable OTLP if SigNoz health check fails repeatedly
5. **Metrics-only mode**: Disable traces/logs but keep metrics export for lightweight monitoring

## Related Documentation

- **Task 08**: `Tasks-M1.md` § Infrastructure Telemetry
- **Admin Guide**: `Guide-For-Admins-and-Tenants.md` § 8.3 Infrastructure Telemetry
- **Completion Summary**: `docs/Task08-Infrastructure-Telemetry-Completion.md`
- **OpenTelemetry Extensions**: `TansuCloud.Observability.Shared/OpenTelemetryExtensions.cs`

## Conclusion

This feature provides production-grade resilience for the Telemetry service by allowing operators to disable OTLP export when appropriate. The service continues to fulfill its primary purpose (ingesting logs from other services) regardless of SigNoz availability, while still offering full observability in development environments.

**Status**: ✅ Implemented and verified (2025-10-10)
