# SigNoz API Integration Guide

## Overview

This guide describes how to integrate SigNoz observability data into the TansuCloud Admin Dashboard's Observability pages using the SigNoz REST API.

## Current State

- **Development**: SigNoz UI is accessible directly at `http://127.0.0.1:3301`
- **Production**: SigNoz is not exposed through the gateway; access is restricted to infrastructure administrators
- **Gateway**: SigNoz reverse proxy routes have been removed as of this document

## SigNoz API Endpoints

SigNoz provides REST APIs for programmatic access to observability data. The primary API endpoints are:

### Query Service API

Base URL (development): `http://signoz-query-service:8080/api/v3` (internal Docker network)
Base URL (production): Configure via environment variable `SIGNOZ_API_BASE_URL`

Key endpoints:

1. **Traces Query**
   - `POST /api/v3/query_range` - Query traces with filters
   - `GET /api/v1/traces/{traceId}` - Get specific trace by ID
   - `POST /api/v1/traces/aggregate` - Aggregate trace data

2. **Metrics Query**
   - `POST /api/v2/metrics/query_range` - Query metrics time series
   - `GET /api/v1/metrics` - List available metrics
   - `POST /api/v1/metrics/autocomplete` - Autocomplete metric names

3. **Logs Query**
   - `POST /api/v1/logs` - Query logs with filters
   - `POST /api/v1/logs/aggregate` - Aggregate log data
   - `GET /api/v1/logs/fields` - Get available log fields

4. **Service Map & Topology**
   - `GET /api/v1/service/list` - List all services
   - `GET /api/v1/service/{serviceName}` - Get service details
   - `GET /api/v1/service/dependencies` - Get service dependency graph

5. **Alerts**
   - `GET /api/v3/alerts` - List configured alerts
   - `GET /api/v3/alerts/{id}` - Get alert details

## Authentication

SigNoz supports API key authentication. Configure API keys in the SigNoz admin settings:

1. Generate an API key in SigNoz UI → Settings → API Keys
2. Store the key securely (e.g., Azure Key Vault, environment variable)
3. Include in requests: `Authorization: Bearer <api-key>`

## Implementation Approach for Dashboard Integration

### Phase 1: Backend Service (Recommended)

Create a dedicated service or extend the Telemetry service to act as a proxy:

```csharp
// Example: TansuCloud.Telemetry/Services/ISigNozQueryService.cs
public interface ISigNozQueryService
{
    Task<TraceQueryResult> QueryTracesAsync(TraceQueryRequest request, CancellationToken ct);
    Task<MetricsQueryResult> QueryMetricsAsync(MetricsQueryRequest request, CancellationToken ct);
    Task<LogsQueryResult> QueryLogsAsync(LogsQueryRequest request, CancellationToken ct);
    Task<ServiceTopology> GetServiceTopologyAsync(CancellationToken ct);
}
```

Configuration:
```json
{
  "SigNoz": {
    "ApiBaseUrl": "http://signoz-query-service:8080",
    "ApiKey": "<from-environment>",
    "TimeoutSeconds": 30
  }
}
```

### Phase 2: Dashboard UI Components

Add Blazor components in `TansuCloud.Dashboard/Pages/Admin/Observability/`:

- `ServiceMap.razor` - Visualize service dependencies
- `TracesExplorer.razor` - Search and view traces
- `MetricsDashboard.razor` - Display key metrics with charts
- `LogsViewer.razor` - Query and display logs

Use JavaScript interop with charting libraries (Chart.js, Plotly, or similar) for visualizations.

### Phase 3: Caching and Performance

- Cache frequently accessed data (service list, metric names) using Redis
- Use HybridCache for short-lived query results
- Implement pagination for large result sets
- Consider server-side rendering with streaming for large traces

## Sample Query Patterns

### Get Service Latency (Last 1 Hour)

```json
POST /api/v2/metrics/query_range
{
  "query": "histogram_quantile(0.95, sum(rate(http_server_duration_bucket[5m])) by (le, service_name))",
  "start": 1697097600,
  "end": 1697101200,
  "step": 60
}
```

### Query Recent Errors

```json
POST /api/v1/traces/aggregate
{
  "start": 1697097600000,
  "end": 1697101200000,
  "filters": {
    "status": "error"
  },
  "aggregateAttribute": "service_name",
  "aggregateOperator": "count"
}
```

### Search Logs by Service

```json
POST /api/v1/logs
{
  "start": 1697097600000,
  "end": 1697101200000,
  "filters": [
    {
      "key": "service_name",
      "value": "tansu.database",
      "op": "="
    }
  ],
  "limit": 100
}
```

## Security Considerations

1. **Network Isolation**: Keep SigNoz query service on the internal Docker network only
2. **Authentication**: Always use API keys; rotate them regularly
3. **Authorization**: Dashboard must verify admin role before exposing SigNoz data
4. **Rate Limiting**: Implement rate limits on the proxy service to prevent abuse
5. **Query Validation**: Sanitize and validate all query parameters to prevent injection
6. **Audit Logging**: Log all SigNoz API calls with user context

## Production Deployment

### Option 1: Internal Service Proxy (Recommended)

```yaml
# docker-compose.prod.yml (add to services)
signoz-query-service:
  image: signoz/query-service:latest
  networks:
    - tansu-internal
  environment:
    - CLICKHOUSE_HOST=clickhouse
    - SIGNOZ_API_KEY=${SIGNOZ_API_KEY}
  # No ports exposed to host
```

Dashboard/Telemetry service calls `http://signoz-query-service:8080` internally.

### Option 2: Separate Admin Subdomain

If operators need full SigNoz UI access:

```nginx
# nginx example
location /observability/ {
    auth_request /auth-admin;  # Verify admin role
    proxy_pass http://signoz:3301/;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
}
```

Requires custom authentication middleware or OAuth2 proxy.

## Next Steps

1. **Spike**: Create a proof-of-concept in `TansuCloud.Telemetry` to query SigNoz API
2. **UI Mockup**: Design Admin Dashboard Observability page layouts
3. **API Contract**: Define DTOs and service interfaces for SigNoz integration
4. **Implementation**: Build service proxy, UI components, and tests
5. **Documentation**: Update `Guide-For-Admins-and-Tenants.md` with new features

## References

- [SigNoz Documentation](https://signoz.io/docs/)
- [SigNoz Query API](https://signoz.io/docs/userguide/query-builder/)
- [ClickHouse SQL Reference](https://clickhouse.com/docs/en/sql-reference/) (SigNoz stores data in ClickHouse)
- [OpenTelemetry Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/)

## Related Tasks

This work aligns with the following task areas:

- **Milestone 4, Task 38+**: Observability and telemetry infrastructure
- **Future**: Admin Dashboard enhancements for operational visibility
- **Future**: Multi-tenant observability (scope queries by tenant)

---

*Last updated: 2025-10-12*
*Author: TansuCloud Team*
