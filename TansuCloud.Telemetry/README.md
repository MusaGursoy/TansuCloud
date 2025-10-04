# TansuCloud Telemetry Service

The *TansuCloud.Telemetry* service accepts product telemetry batches from customer deployments, buffers them through an in-memory queue, and persists each report into a local SQLite store. Operators can monitor ingestion readiness via health checks and metrics, and manage envelopes through the built-in Razor admin console.

## Capabilities

- **Secure ingestion endpoint** &mdash; `POST /api/logs/report` guarded by a bearer API key.
- **Backpressure-aware queue** &mdash; bounded channel with configurable capacity and timeout.
- **SQLite persistence** &mdash; envelopes and items stored with relational links for efficient filtering.
- **Background writer** &mdash; single writer drains the queue sequentially to honor SQLite constraints.
- **Observability** &mdash; OpenTelemetry traces/metrics/logs plus `/health/live` and `/health/ready` endpoints.
- **Razor admin console** &mdash; `/admin` presents filters, metrics, and drill-down views protected by the admin API key.

## Configuration

Options are bound from the `Telemetry` section (environment variables use `__` as the separator).

| Setting | Description | Default |
| --- | --- | --- |
| `Telemetry:Ingestion:ApiKey` | Bearer token required by reporters. **Must be overridden per environment.** | `replace-with-secure-key-please-change` |
| `Telemetry:Ingestion:QueueCapacity` | Maximum number of pending batches in memory. | `4096` (Dev overrides to `1024`) |
| `Telemetry:Ingestion:EnqueueTimeout` | Max wait for queue space before returning 503. | `00:00:05` |
| `Telemetry:Database:FilePath` | SQLite database path (relative paths resolved under the content root). | `App_Data/telemetry/telemetry.db` |
| `Telemetry:Database:EnforceForeignKeys` | Enables SQLite foreign key enforcement. | `true` |

> **API key hint:** set the same value in Dashboard’s `LogReportingOptions` so reporters authenticate successfully.

## Local development

```pwsh
# From the repo root
$env:TELEMETRY__INGESTION__APIKEY = "dev-telemetry-api-key-1234567890"
$env:TELEMETRY__DATABASE__FILEPATH = "$PWD\App_Data\telemetry\telemetry.dev.db"
dotnet run --project .\TansuCloud.Telemetry\TansuCloud.Telemetry.csproj -c Debug
```

Health checks:

- Liveness: <http://127.0.0.1:5279/health/live>
- Readiness: <http://127.0.0.1:5279/health/ready>

Sample ingestion call:

```pwsh
$payload = @'
{
  "host": "gateway-dev",
  "environment": "Development",
  "service": "tansu.dashboard",
  "severityThreshold": "Warning",
  "windowMinutes": 5,
  "maxItems": 200,
  "items": [
    {
      "kind": "log",
      "timestamp": "${((Get-Date).ToUniversalTime()).ToString("o")}",
      "level": "Warning",
      "message": "Sample warning message",
      "templateHash": "hash123",
      "exception": null,
      "service": "tansu.dashboard",
      "environment": "Development",
      "tenantHash": "tenant-abc",
      "correlationId": "corr-123",
      "traceId": "trace-123",
      "spanId": "span-123",
      "category": "SampleCategory",
      "eventId": 1051,
      "count": 1,
      "properties": {
        "userId": "demo-user",
        "feature": "demo"
      }
    }
  ]
}
'@

Invoke-RestMethod -Method Post `
  -Uri http://127.0.0.1:5279/api/logs/report `
  -Headers @{ Authorization = "Bearer dev-telemetry-api-key-1234567890" } `
  -Body $payload `
  -ContentType "application/json"
```

The service responds with `202 Accepted` when the batch is queued. A `503` indicates the queue is full; retry with exponential backoff.

## Admin console

- UI entry point: `GET /admin` (aliases `/admin/envelopes` and `/admin/envelopes/{id}`). Every request requires `Authorization: Bearer <Telemetry__Admin__ApiKey>`.
- Dev compose already exposes the console on <http://127.0.0.1:5279/admin> and ships a sample admin key in `appsettings.Development.json` / `.env`. For production, inject the header via your reverse proxy (see *Guide-For-Admins-and-Tenants.md* §9.2 for Nginx/Traefik examples).
- Feature highlights:
  - Filter envelopes by service, environment, host, severity floor, free-text search, date range, acknowledgement, and archive state.
  - Inline metrics summarise active/acknowledged/archived envelopes and total log items on the current page.
  - Drill into envelope detail pages to inspect individual log records, structured properties, and exception text.
  - Acknowledge or archive envelopes from either the list or detail view; the UI disables buttons once the state changes.
- Quick CLI smoke check:

  ```pwsh
  curl.exe -H "Authorization: Bearer $env:TELEMETRY__ADMIN__APIKEY" http://127.0.0.1:5279/api/admin/envelopes?page=1 | jq .totalCount
  ```

  Replace the host with your public endpoint when testing through a proxy.

## Docker usage

Build locally:

```pwsh
docker build -f TansuCloud.Telemetry/Dockerfile -t tansu/telemetry:local .
```

Run the container (persist SQLite to the host):

```pwsh
docker run --rm `
  -p 5279:8080 `
  -e TELEMETRY__INGESTION__APIKEY=dev-telemetry-api-key-1234567890 `
  -e TELEMETRY__DATABASE__FILEPATH=/var/opt/tansu/telemetry/telemetry.db `
  -v $PWD/data/telemetry:/var/opt/tansu/telemetry `
  tansu/telemetry:local
```

Health endpoints are exposed on the mapped port (`/health/live`, `/health/ready`).

## Operations notes

- Monitor queue depth via the readiness health check payload (`queueUsage`) and OpenTelemetry metric `telemetry_queue_depth`.
- SQLite database files should be included in regular backups. The recommended production deployment uses a dedicated service instance with the volume mounted to durable storage.
- Ensure the admin console is reachable only through a trusted proxy that stamps the admin bearer token; combine with VPN/IP allowlists. Bulk exports are still on the roadmap—use the `/api/admin/envelopes` JSON endpoints for scripting in the meantime.
- Disable ingestion by rotating the API key and toggling reporters off; unauthenticated requests will receive HTTP 401.
