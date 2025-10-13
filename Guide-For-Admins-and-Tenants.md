# Tansu.Cloud Admin & Tenant Guide

## Overview

Tansu.Cloud is a modern backend-as-a-service (BaaS) platform built on .NET, providing identity, API gateway, dashboards, database and storage services, observability, and health management. This guide helps admins and tenant operators deploy, configure, and operate the platform.

## 1. Getting Started

- Architecture at a glance
- Core services and responsibilities
- Environments (dev, staging, production)

## 2. Prerequisites

- Docker and Docker Compose
- Certificates (PFX) for TLS
- DNS and firewall basics

## 3. Quickstart: Local Development

- Run all services with VS Code tasks (dev: up / dev: down)
- Running with Docker Compose (gateway only exposed)
- Health endpoints (/health/live and /health/ready)

### 3.1 Load `.env` once across shells

All automation (VS Code tasks, helper scripts, compose runs) now pulls public and gateway URLs from the shared `.env`. When you run one-off commands outside VS Code, hydrate your shell first so both Windows and macOS/Linux sessions stay in sync.

- **PowerShell (Windows)**

```powershell
pwsh -NoProfile -Command ". \"${PWD}/dev/tools/common.ps1\"; Import-TansuDotEnv | Out-Null; docker compose --env-file \"${PWD}/.env\" up -d"
```

- **PowerShell (macOS/Linux)**

```powershell
pwsh -NoProfile -Command ". \"${PWD}/dev/tools/common.ps1\"; Import-TansuDotEnv | Out-Null; docker compose --env-file \"${PWD}/.env\" up -d"
```

Notes

- `Import-TansuDotEnv` keeps existing process variables unless you pass `-Overwrite`; this means ad-hoc overrides survive.
- Compose commands should always pass `--env-file ./ .env` (our tasks already do) so container-side substitutions stay aligned with the host values.
- For Bash/zsh users, launch `pwsh` once to run the helper, or export variables manually by mirroring `.env`.
- CI enforces the `.env`-first contract via `LoopbackLiteralGuardTests`. If you need to document loopback literals (as in this section), keep them in the documented allowlist to avoid test failures.
- After editing `.env`, run the VS Code task **"Task 40 verification"** to execute the loopback guard test and validate both compose files against the refreshed settings.

## 4. Production Topology

- Gateway-only exposure (80/443)
- Internal service networking
- OpenTelemetry collector

### 4.2 Running production with a single compose file

We ship exactly two compose files:

- `docker-compose.yml` — development stack (local conveniences, dev ports)
- `docker-compose.prod.yml` — full production stack (gateway exposed) with an optional `observability` profile

Before running any compose command, ensure `.env` contains the correct `PUBLIC_BASE_URL`, `GATEWAY_BASE_URL`, certificate secrets, and other overrides. Our VS Code tasks call `Import-TansuDotEnv` automatically; shells launched manually should do the same so compose sees the intended values.

Run without observability (core services only):

```powershell
docker compose -f docker-compose.prod.yml up -d --build
```

```yaml
services:
  gateway:
    image: tansucloud-gateway
    ports:
      - "80:8080"    # HTTP
      - "443:8443"   # HTTPS (container listens on 8443)
    volumes:
      - ./certs:/certs:ro
    environment:
      - Kestrel__Endpoints__Https__Url=https://0.0.0.0:8443
      - Kestrel__Endpoints__Https__Certificate__Path=/certs/gateway.pfx
      - GATEWAY_CERT_PASSWORD=${GATEWAY_CERT_PASSWORD}
    depends_on:
      identity:
        condition: service_healthy
      dashboard:
        condition: service_healthy
      db:
        condition: service_started
    # Optional: forward TLS from gateway to downstream services
    extra_hosts:
      - "identity:127.0.0.1"
    command: >-
      dotnet TansuCloud.Gateway.dll
      --urls "http://0.0.0.0:8080;https://0.0.0.0:8443"
      --config gateway.prod.json
  dashboard:
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:5000
      - DASHBOARD_PUBLIC_BASE_URL=https://apps.example.com/dashboard
  identity:
    environment:
      - ASPNETCORE_URLS=http://0.0.0.0:5000
      - ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
  # … other services …
```

Ensure X-Forwarded-Proto and X-Forwarded-Prefix headers are preserved when TLS terminates upstream to avoid OIDC issuer mismatches or cookie scope issues.

### 4.3 Configure your public URLs (Task 40)

TansuCloud derives all browser-visible links and OIDC issuers from configuration. Do not hardcode URLs in code. Set these two keys before starting any environment:

- PUBLIC_BASE_URL — the single origin browsers see (e.g., <http://127.0.0.1:8080> in dev, <https://apps.example.com> in prod)
- GATEWAY_BASE_URL — the internal or operator-friendly base for the gateway when different from PUBLIC_BASE_URL (often the same in dev)

Recommended: define them in `.env` at the repo root. Our tasks and compose files automatically load `.env`.

Example `.env` (development):

```env
PUBLIC_BASE_URL=http://127.0.0.1:8080
GATEWAY_BASE_URL=http://127.0.0.1:8080
```

Example `.env` (production):

```env
PUBLIC_BASE_URL=https://apps.example.com
GATEWAY_BASE_URL=http://gateway:8080
```

Notes

- Services compute OIDC values from these URLs. Identity issues tokens with issuer `${PUBLIC_BASE_URL}/identity/`.
- Dashboard and APIs discover via the gateway when running in containers using MetadataAddress overrides as documented in the OIDC standard below.
- If you change `.env`, re-run the VS Code task “Task 40 verification” to validate the loopback guard and compose files.
- Keep loopback literals (127.0.0.1) in docs/examples only. Code should never inline them; derive from configuration.

Quick validation

1. Validate compose files with your `.env` values:

- VS Code task: “compose: validate config” (dev)
- VS Code task: “compose: validate prod config” (prod)

1. Check Identity discovery at `${PUBLIC_BASE_URL}/identity/.well-known/openid-configuration`.

1. Through the gateway, open:

- `${PUBLIC_BASE_URL}/dashboard/health/ready`
- `${PUBLIC_BASE_URL}/identity/health/ready`
- `${PUBLIC_BASE_URL}/db/health/ready`
- `${PUBLIC_BASE_URL}/storage/health/ready`

Common pitfalls

- Mismatch between Issuer and discovery host: ensure Identity’s advertised issuer matches `${PUBLIC_BASE_URL}/identity/` exactly (trailing slash included).
- Missing X-Forwarded headers at the edge: proxies must preserve `Host`, and set `X-Forwarded-Proto`/`X-Forwarded-Prefix` so downstream apps compute correct redirects and cookie scopes.
- Mixing localhost and 127.0.0.1: prefer 127.0.0.1 in dev to avoid IPv6/loopback divergence.

### 8.2 SigNoz metrics & dashboards (Unified Observability)

We use SigNoz as the single UI for metrics, traces, and logs.

**Development**: The SigNoz UI is available at [http://127.0.0.1:3301/](http://127.0.0.1:3301/) via direct port mapping in docker-compose.yml.

**Production**: SigNoz is not exposed through the gateway or to end users. For production deployments:

- Restrict SigNoz access to infrastructure administrators via secure network policies
- Consider embedding SigNoz observability data into the Admin Dashboard's Observability pages using the SigNoz REST API
- Alternatively, expose SigNoz on a separate subdomain with appropriate authentication

All services export OTLP to the in-cluster SigNoz collector. Metrics, traces, and logs appear automatically as you drive traffic through the gateway.Quick start (dev)

1) Bring up infra and apps (VS Code tasks: "compose: up infra (pg + garnet + pgcat)" then "compose: up apps").
2) Drive some traffic: open `/storage/health/ready` and `/db/health/ready` a few times via the gateway.
3) Open the SigNoz UI at [http://127.0.0.1:3301/](http://127.0.0.1:3301/) and explore:
   - Metrics: Service graphs (requests/sec, error rate, latency percentiles)
   - Traces: End-to-end spans per request

- Logs: Centralized structured logs across services
- Infrastructure: Postgres, Garnet, and PgCat dashboards now surface automatically through the shared collector (pool saturation, cache hit rate, connection counts)

Legacy in-app charts (Prometheus proxy)

- The Dashboard used to proxy Prometheus and render curated charts at `/dashboard/admin/metrics`.
- That Prometheus proxy has been retired. The Metrics page now renders a curated SigNoz catalog using the configured base URL (default `http://127.0.0.1:3301/`).
- Admin APIs under `/dashboard/api/metrics/*` remain available for smoke tests and integrations; they return SigNoz metadata (catalog entries and redirect URLs) instead of raw Prometheus payloads.

Notes

- Override the SigNoz base URL with `SigNoz:BaseUrl` (or env var `SigNoz__BaseUrl`) to align with your deployment.
- `/dashboard/api/metrics/catalog` responds with JSON containing the normalized base URL plus the curated chart list, so automation can deep-link into SigNoz.

### SigNoz readiness and troubleshooting (dev)

Use this quick section to verify the dev SigNoz stack is ready before running observability E2Es.

Readiness checks

- OTLP gRPC (collector): `<http://127.0.0.1:4317>` reachable from apps and the host (our E2E will soft-skip if not)
- ClickHouse HTTP: `<http://127.0.0.1:8123>` reachable (used by tests to query spans/logs)
- SigNoz UI: `<http://127.0.0.1:3301>` opens without errors

Dev validation route

- Trigger an intentional exception through the Gateway to Storage:
  - `GET http://127.0.0.1:8080/storage/dev/throw?message=hello`
  - In Development, this route is anonymous and logs an Error before throwing, ensuring both log and trace signals.

Span attribute smoke

- Confirm gateway HTTP spans carry the expected tags by running the dedicated E2E:

  ```pwsh
  dotnet test tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~AspNetCoreSpanAttributesE2E
  ```

  The test calls `/health/ready` with a temporary `X-Tansu-Tenant` header and polls `signoz_traces.signoz_index_v3` to assert `http.route`, `http.status_code`, `tansu.route_base`, and `tansu.tenant` attributes are present. It prints `[SKIP]` when the OTLP collector (default `127.0.0.1:4317`) is not reachable yet.

Tables of interest (ClickHouse)

- Traces: `signoz_traces.signoz_index_v3` (includes alias columns like `serviceName`)
- Logs: `signoz_logs.logs_v2` (`resources_string` / `attributes_string` maps)

Environment variables (defaults)

- `GATEWAY_BASE_URL=http://127.0.0.1:8080`
- `CLICKHOUSE_HTTP=http://127.0.0.1:8123`
- `CLICKHOUSE_USER=admin` / `CLICKHOUSE_PASSWORD=admin`
- `OTLP_GRPC_HOST=127.0.0.1` / `OTLP_GRPC_PORT=4317`

Dev vs Prod port exposure

- Development compose publishes OTLP ports from the collector to the host: `4317:4317` (gRPC) and `4318:4318` (HTTP). This allows local tests and tools to connect via `127.0.0.1`.
- Production compose keeps the collector internal-only under the optional `observability` profile (no host port mapping). Services send telemetry to `http://signoz-otel-collector:4317` inside the network.

Production notes

- Keep `/storage/dev/throw` strictly Development-only.
- For production, add TLS, SSO/RBAC for the SigNoz UI, retention/backups/HA for ClickHouse, and appropriate sampling/PII scrubbing policies.

### Apply SigNoz governance defaults (Task 39)

We ship opinionated governance defaults under `SigNoz/governance.defaults.json` (retention windows, sampling ratios, and SLO alert templates). These are applied via VS Code tasks and a helper script. By default, the script runs in a safe dry‑run mode and prints the planned actions.

Prerequisites

- Ensure the SigNoz stack is healthy (see readiness section above), or run the VS Code task “SigNoz: readiness probe”.
- Set a ClickHouse HTTP endpoint in your environment (recommended in `.env`):
  - Dev example: `CLICKHOUSE_HTTP=http://127.0.0.1:8123`
  - Prod example: internal-only endpoint for ClickHouse HTTP on your cluster network (do not expose publicly)

Run the tasks

- Preview (dry‑run): VS Code task “SigNoz: governance (dry-run)”
  - Loads `SigNoz/governance.defaults.json`
  - Verifies `CLICKHOUSE_HTTP` is reachable
  - Prints the planned retention/sampling/alert SLOs without making changes
- Apply (when enabled): VS Code task “SigNoz: governance (apply)”
  - The apply stage is intentionally not implemented yet to avoid accidental destructive changes. Use the dry‑run to validate values first; we’ll wire safe apply hooks once finalized.

Notes

- You can override the defaults by editing `SigNoz/governance.defaults.json` in your fork/overlay. Keep production values conservative (longer retention has storage cost implications).
- The helper script `dev/tools/signoz-apply-governance.ps1` reads environment via `.env` using `Import-TansuDotEnv`. Prefer environment-driven endpoints to avoid hardcoding URLs.
- Dashboard’s observability pages link to SigNoz for deep dives; thin status surfaces remain in-app.

#### SigNoz UI validation (quick checks)

Use these quick checks after `docker compose up` to confirm SigNoz is healthy:

- Open `<http://127.0.0.1:3301/>` and ensure the landing page loads without errors.
- Navigate to Services → a service like `TansuCloud.Gateway` should appear under load; open it and see request rate, latency, and error charts.
- Navigate to Traces → Explorer; run a search for the last 15 minutes. You should see spans from Gateway/Identity/Database/Storage under light traffic.
- Navigate to Logs → Recent; filter by `service.name="TansuCloud.Gateway"` and confirm new entries appear while driving requests.
- Navigate to Dashboards → Host (or Infrastructure) and verify CPU/Memory charts populate. This is powered by the dev-only hostmetrics receiver.
- Optional sanity queries (via ClickHouse client inside the network):
  - `SELECT count() FROM signoz_metrics.time_series_v4` → should be non-zero and increasing.
  - `SHOW CREATE TABLE signoz_traces.signoz_index_v3` → contains `resource.service.name` alias and `resource_string_service$$name_exists`.

Troubleshooting — SigNoz migrations and metrics tables

- Symptom: SigNoz UI shows no metrics; ClickHouse `signoz_metrics` DB is missing v4 time-series tables like `time_series_v4`, `samples_v4`, or migrations report `UNKNOWN_TABLE` on `time_series_v4`.
- Cause: When migrations run in certain dev modes, “squashed” base migrations may be skipped and later ALTERs (e.g., migration 1000+) fail because base tables weren’t created yet.
- Fix (dev compose):
  1. Drop the metrics DB to reset state across the single-node cluster: `DROP DATABASE IF EXISTS signoz_metrics ON CLUSTER cluster`.
  2. Re-run the schema migrator once so squashed migrations materialize all v4 tables. In our compose, the `schema-migrator-sync` service runs automatically; to force a one-off run:
      - `docker run --rm --network tansucloud_tansucloud-network signoz/signoz-schema-migrator:latest sync --dsn=tcp://admin:admin@clickhouse:9000`
  3. Verify tables exist: `SHOW TABLES FROM signoz_metrics` should list `time_series_v4`, `time_series_v4_6hrs`, `time_series_v4_1day`, `time_series_v4_1week` and their distributed/materialized views, plus `samples_v4*`.
  4. Send traffic (or wait for hostmetrics) and confirm new rows: `SELECT count() FROM signoz_metrics.time_series_v4` and `SELECT count() FROM signoz_metrics.samples_v4`.

Troubleshooting — Services page 500 (distributed_top_level_operations missing)

- Symptom: Services page shows "No data" and `POST /api/v1/services` returns 500.
- Logs: SigNoz query service reports `Unknown table signoz_traces.distributed_top_level_operations`.
- Cause: Older/squashed migrations combined with single-node cluster setups sometimes miss creating this distributed object.

Fix (dev compose)

1. Validate base table exists and has recent spans:

    - `SHOW TABLES FROM signoz_traces LIKE 'distributed_signoz_index_v3';`
    - `SELECT count() FROM signoz_traces.distributed_signoz_index_v3 WHERE timestamp > now() - INTERVAL 15 MINUTE;`

1. Ensure the compatibility view exists (exposed for SigNoz Services API):

    - `SHOW TABLES FROM signoz_traces LIKE 'distributed_top_level_operations';`
    - `DESCRIBE TABLE signoz_traces.distributed_top_level_operations;` (expect columns: `name`, `serviceName`, `time`)

1. Our dev compose includes an init container `signoz-clickhouse-compat-init` that creates the view idempotently:

    ```sql
    CREATE VIEW IF NOT EXISTS signoz_traces.distributed_top_level_operations ON CLUSTER cluster AS
    SELECT name, serviceName, toDateTime(timestamp) AS time
    FROM signoz_traces.distributed_signoz_index_v3
    WHERE (parent_span_id = '' OR length(parent_span_id) = 0);
    ```

Notes

- This is a dev-only compatibility shim to unblock the Services page. In production, rely on SigNoz-managed migrations and ensure the ClickHouse cluster name is configured consistently so ON CLUSTER DDLs create distributed objects.

ON CLUSTER caveats (single-node dev)

- Our ClickHouse config defines a single-node cluster named `cluster` (see `dev/clickhouse/cluster.xml`). Migrations use `ON CLUSTER cluster` DDLs. This is safe in dev; do not change the cluster name unless you update the migrator/config accordingly.
- If you run ad‑hoc ClickHouse clients from your host, ensure you execute against the container hostname `clickhouse` on the compose network and authenticate with `admin/admin`:
  - Example: `docker run --rm --network tansucloud_tansucloud-network clickhouse/clickhouse-client:latest --host clickhouse --user admin --password admin --query "SHOW TABLES FROM signoz_metrics"`
- If a previous failed migration set a row in `signoz_metrics.schema_migrations_v2` to `failed`, the migrator will retry once the base tables exist. You usually don’t need to edit migration rows manually.

## Health endpoints and startup probes

All core services implement standardized health endpoints and Compose startup gating for reliable boot order and troubleshooting.

- Endpoints per service
  - Liveness: `GET /health/live` → returns 200 when the service process is up. Only checks tagged "self" are evaluated (no external dependencies).
  - Readiness: `GET /health/ready` → returns 200 when required dependencies (Garnet, Postgres via PgCat) are reachable. Only checks tagged "ready" are evaluated.

- Via Gateway
  - Health endpoints are reachable through the Gateway under each service base path:
    - `/identity/health/{live|ready}`
    - `/dashboard/health/{live|ready}`
    - `/db/health/{live|ready}`
    - `/storage/health/{live|ready}`

- Docker Compose gating
  - identity waits for pgcat healthy
  - db waits for pgcat and garnet healthy
  - storage waits for garnet healthy
  - dashboard waits for garnet healthy
  - gateway waits for identity, db, storage, and dashboard to be healthy
  - PgCat healthcheck uses a CMD-SHELL form that checks `/proc/1/cmdline` to avoid image differences (busybox vs bash) and flakiness.

Tips

- Check container health quickly with: `docker inspect --format "{{json .State.Health}}" <container>`
- Readiness may intentionally be Unhealthy while a dependency is down; liveness should remain Healthy so restarts aren’t masked by dependency outages.

### Quick curl examples (via Gateway)

Check gateway’s own readiness

```bash
curl -fsS http://127.0.0.1:8080/health/ready | jq .
```

Check service liveness/readiness through gateway base paths

```bash
# Identity
curl -fsS http://127.0.0.1:8080/identity/health/live | jq .
curl -fsS http://127.0.0.1:8080/identity/health/ready | jq .

# Database API
curl -fsS http://127.0.0.1:8080/db/health/live | jq .
curl -fsS http://127.0.0.1:8080/db/health/ready | jq .

# Storage API
curl -fsS http://127.0.0.1:8080/storage/health/live | jq .
curl -fsS http://127.0.0.1:8080/storage/health/ready | jq .

# Dashboard
curl -fsS http://127.0.0.1:8080/dashboard/health/live | jq .
curl -fsS http://127.0.0.1:8080/dashboard/health/ready | jq .
```

Notes

- Replace 127.0.0.1:8080 with your PublicBaseUrl host/port in other environments.
- jq is optional; omit it if not installed.

Collector configuration

- The SigNoz OpenTelemetry Collector config is stored at `dev/signoz-otel-collector-config.yaml`. It now enables:
  - Receivers: `otlp` (gRPC+HTTP), `hostmetrics` (cpu, load, memory, filesystem, network) every 15s, `prometheus/postgres` (PostgreSQL metrics via postgres-exporter on port 9187), and `prometheus/redis` (Redis metrics via redis-exporter on port 9121).
  - Exporters: `clickhousetraces` and `clickhouselogsexporter` with `use_new_schema: true`, and `signozclickhousemetrics` to `signoz_metrics`.
  - Pipelines: traces, logs, and metrics wired to the above exporters so infra and app metrics share the same dashboards.

  Compose ensures the collector receives `POSTGRES_USER` and `POSTGRES_PASSWORD` from `.env`. In production replace these with a low-privilege monitoring role and rotate credentials via secret management. Redis exporters currently expose unauthenticated read-only metrics in dev; secure them behind auth or network policies before shipping to production.

Telemetry service OTLP configuration (production resilience)

- The [`TansuCloud.Telemetry`](TansuCloud.Telemetry ) service can optionally disable OTLP export in production to avoid circular dependencies (Telemetry ingests logs from other services; we don't want it trying to send its own logs to SigNoz if SigNoz is unavailable).
- Configuration keys:
  - `OpenTelemetry:Otlp:Enabled` (bool, default true in dev, can be set to false in production)
  - `OpenTelemetry:Otlp:Endpoint` (string, default http://signoz-otel-collector:4317)
- If `Enabled=false`, the Telemetry service will skip OTLP export entirely and continue operating normally, storing ingested logs in its local database without attempting to send telemetry to SigNoz.
- This ensures graceful degradation: if SigNoz is down or unreachable, Telemetry continues to accept and store logs from other services without blocking or failing.
- Environment variable override: `OpenTelemetry__Otlp__Enabled=false`
- In production, consider disabling OTLP for Telemetry to keep it self-contained and avoid potential feedback loops.

1. Hardening (recommended)

- Enable HSTS, fine‑tune CORS allowlists, and use secret stores for certificate passwords.

### 5.1 Database Schema Management and Infrastructure Validation (Task 43)

TansuCloud now includes automated schema management and infrastructure validation to ensure all required databases are in a known, valid state before services accept HTTP traffic. This provides safe, reliable deployments and clear operational diagnostics.

#### Overview

The Database service validates and tracks schema state at startup and enriches health checks with infrastructure diagnostics. Key components:

- **DatabaseSchemaHostedService** — startup validation of Identity, Audit, and tenant databases before accepting traffic
- **SchemaVersionService** — tracks schema versions in `__SchemaVersion` table per database
- **PgCatPoolHostedService** — reconciles PgCat connection pools with discovered tenant databases
- **InfrastructureHealthCheck** — enriches `/db/health/ready` with schema validation status, tenant/pool counts, and ClickHouse connectivity

#### Startup Validation Sequence

When the Database service starts:

1. **Identity Database**: Validates `tansu_identity` exists and contains expected OpenIddict tables. Records schema version if validation passes.
2. **Audit Database**: Validates `tansu_audit` exists with `audit_events` table and indexes. Records schema version if validation passes.
3. **Tenant Databases**: Discovers all `tansu_tenant_*` databases, validates each has required tables (`documents`, `embeddings`, `schema_migrations`), and records schema versions.
4. **ClickHouse Connectivity**: Performs a read-only probe to ClickHouse (used by SigNoz). This is informational only and does not fail startup.
5. **PgCat Pool Reconciliation**: (Background) Discovers tenant databases and ensures PgCat has corresponding connection pools configured.

If any critical validation fails (Identity, Audit, or tenant schemas are invalid), the service logs errors and `/db/health/ready` reports `Unhealthy`. The service will not accept API requests until schemas are valid.

#### Schema Version Tracking

Each validated database gets a row in its own `__SchemaVersion` table with:

- `Version` — semantic version string (e.g., "1.0.0")
- `AppliedAtUtc` — timestamp of validation
- `AppliedBy` — source service (e.g., "TansuCloud.Database")
- `Description` — human-readable note

This provides an audit trail of schema state and supports future migration workflows.

#### Health Check Enrichment

The `/db/health/ready` endpoint now includes infrastructure diagnostics:

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.123",
  "entries": {
    "infrastructure": {
      "status": "Healthy",
      "description": "Schema validation: Healthy. Tenants: 3. PgCat pools: 3. ClickHouse: Connected.",
      "data": {
        "schemaValidationStatus": "Healthy",
        "tenantCount": 3,
        "pgcatPoolCount": 3,
        "clickhouseConnected": true
      }
    }
  }
}
```

Use this to monitor:

- **Schema validation status** — Healthy if all required databases have valid schemas
- **Tenant count** — number of provisioned tenant databases
- **PgCat pool count** — number of connection pools configured (should match tenant count)
- **ClickHouse connectivity** — whether SigNoz backend is reachable (informational)

#### Operational Procedures

**Initial Setup (New Deployment)**

1. Ensure PostgreSQL is running and accessible (see `POSTGRES_*` variables in `.env`).
2. Run Identity service first to seed `tansu_identity` with OpenIddict tables.
3. Run Database service—it will validate Identity and Audit schemas, create `tansu_audit` if missing, and record schema versions.
4. Provision tenants via `/db/api/provisioning/tenants` (POST with `X-Provision-Key`).
5. Verify health: `curl http://127.0.0.1:8080/db/health/ready | jq .entries.infrastructure`

**Adding a New Tenant**

1. POST to `/db/api/provisioning/tenants` with tenant ID and display name.
2. Database service creates `tansu_tenant_<id>` with required schema.
3. PgCatPoolHostedService (background) discovers the new database and adds a PgCat pool.
4. Health check `tenantCount` and `pgcatPoolCount` increment automatically.

**Schema Drift Detection**

- If a tenant database is missing required tables, startup validation logs errors and health check reports `Unhealthy`.
- Review logs: `docker logs <database-container> | grep "Schema validation failed"`
- Fix: restore missing tables from a backup or re-provision the tenant (destructive—use with care).

**PgCat Pool Reconciliation**

- PgCatPoolHostedService runs every 60 seconds (configurable via `Database:PgCat:ReconcileIntervalSeconds`).
- If a tenant database exists but has no PgCat pool, the service logs a warning and adds the pool dynamically (if PgCat is configured).
- Health check `pgcatPoolCount` shows current pool count. If it's less than `tenantCount`, check logs for reconciliation errors.

**ClickHouse Connectivity**

- DatabaseSchemaHostedService probes ClickHouse at startup using `ClickHouse:Http` configuration.
- If unreachable, logs a warning but does not fail startup (SigNoz is optional for Database API operation).
- Health check `clickhouseConnected` is `false` if probe failed. This is informational; Database API remains operational.

**Troubleshooting**

- **Health check shows `Unhealthy` for `infrastructure`:**
  - Check `description` and `data` fields in the health response for details.
  - Common causes: Identity database missing OpenIddict tables, Audit database missing `audit_events`, tenant database missing required schema.
  - Review Database service logs for "Schema validation failed" entries.

- **`pgcatPoolCount` is less than `tenantCount`:**
  - PgCat reconciliation may be disabled or failing. Check `Database:PgCat:Enabled` configuration.
  - Review logs for "PgCat pool reconciliation" warnings.
  - Manually verify PgCat config and restart if needed.

- **`clickhouseConnected` is `false`:**
  - Ensure ClickHouse/SigNoz is running and `ClickHouse:Http` points to the correct endpoint (e.g., `http://clickhouse:8123` in compose).
  - This does not affect Database API functionality—only observability queries are impacted.

**Configuration Reference**

- `ConnectionStrings:DefaultConnection` — PostgreSQL connection string for system databases and tenant discovery
- `Database:PgCat:Enabled` — enable/disable PgCat pool reconciliation (default `true` in dev/compose)
- `Database:PgCat:ReconcileIntervalSeconds` — interval between pool reconciliation runs (default `60`)
- `ClickHouse:Http` — ClickHouse HTTP endpoint for connectivity probe (e.g., `http://clickhouse:8123`)
- `Audit:ConnectionString` — connection string for audit database (e.g., `Host=postgres;Database=tansu_audit;...`)

See also: `docs/DatabaseSchemas.md` for authoritative schema definitions and migration history.

### 5.3 How to provide the certificate password

Provide the password only via the environment variable `GATEWAY_CERT_PASSWORD`. Do not store it in appsettings or source control.

Option A — .env file (recommended for local/dev)

1. Create a `.env` file next to `docker-compose.yml` with:

```env
GATEWAY_CERT_PASSWORD=changeit
```

1. Ensure your gateway service `environment` section contains:

```yaml
environment:
  - Kestrel__Endpoints__Https__Url=https://0.0.0.0:8443
  - Kestrel__Endpoints__Https__Certificate__Path=/certs/gateway.pfx
  - GATEWAY_CERT_PASSWORD=${GATEWAY_CERT_PASSWORD}
```

Docker Compose will automatically load `.env` and substitute the value.

Option B — Shell environment variable (one-off runs, CI/CD)

- Windows PowerShell

```powershell
$env:GATEWAY_CERT_PASSWORD = "changeit"

docker compose up -d
```

- Bash (Linux/macOS)

```bash
export GATEWAY_CERT_PASSWORD=changeit

docker compose up -d
```

Notes

- Don’t commit real passwords into source control. Use `.env` (ignored by default) or secret managers/CI secrets.
- For HTTP-only runs, leave 443 and TLS envs unset, and (in dev) you can set `Gateway:DisableHttpsRedirect=true` to prevent redirects.

## 6. Identity & Authentication

### 6.1 Admin access to Dashboard configuration (Rate Limits)

- Admin-only pages are available after OIDC login with a user in the Admin role (or an access token with the `admin.full` scope).
- To edit Gateway rate limits in development:
  - Navigate to `/dashboard/admin/rate-limits`.
  - Click “Load current”, adjust Window seconds and Default/Route permit/queue limits, and save. Changes apply immediately at the Gateway.
  - In environments where CSRF is enabled, the Dashboard attaches `X-Tansu-Csrf` when the `DASHBOARD_CSRF` environment variable is set.
- In CI/dev E2Es, Identity discovery readiness is checked at `/identity/.well-known/openid-configuration` to avoid flakiness. If discovery is unavailable and strict mode is disabled, UI tests may skip.

- Identity issuer and discovery under /identity
- Configuring OIDC issuer for downstream services (Oidc:Issuer)
- Dashboard OIDC authority configuration

Recommended settings

- Populate `.env` with these keys first (VS Code tasks consume them automatically; `Import-TansuDotEnv` aligns manual shells).
- In production, set `PUBLIC_BASE_URL` to your HTTPS origin in `.env`. The compose file maps this to:
  - Identity Issuer: `${PUBLIC_BASE_URL}/identity/`
  - Dashboard Authority/Metadata: `${PUBLIC_BASE_URL}/identity`
  - Dashboard Redirect URIs:
    - `${PUBLIC_BASE_URL}/dashboard/signin-oidc`
    - `${PUBLIC_BASE_URL}/dashboard/signout-callback-oidc`
  - Optional root variants for alternative routing:
    - `${PUBLIC_BASE_URL}/signin-oidc`
    - `${PUBLIC_BASE_URL}/signout-callback-oidc`
- Set the Dashboard confidential client secret via `DASHBOARD_CLIENT_SECRET` in `.env`.
- The Dashboard now skips the OIDC UserInfo endpoint and relies on the `id_token` for profile/role claims (`GetClaimsFromUserInfoEndpoint=false`). Ensure Identity emits required claims (subject, name, roles) in tokens; no `/connect/userinfo` handler is provisioned.
- Ensure reverse proxy forwarding headers are correct at the gateway; Identity relies on them for issuer/redirects.

Scopes (summary)

- db.read — read access to Database APIs.
- db.write — write access to Database APIs.
- storage.read — read access to Storage APIs.
- storage.write — write access to Storage APIs.
- admin.full — development-only convenience scope implying all of the above.

Gateway login alias

- For convenience, the gateway exposes a login alias at `/login` that redirects to the Identity login UI under `/identity`. This helps operators and testers quickly reach the sign-in form.

### 6.2 Domains & TLS (Admin)

Manage custom domains and TLS certificates centrally from the Dashboard or via the Gateway Admin API.

Admin UI (canonical)

- Navigate to `/dashboard/admin/domains`.
- Actions available:
  - List current domain bindings and certificate metadata.
  - Add/Replace a binding using either:
    - PFX upload (optionally with password), or
    - PEM paste (certificate + private key; optional chain PEM for intermediates).
  - Rotate a binding: provide a new PFX or PEM pair. The API returns both the current and previous certificate metadata to verify rotation.
  - Delete a binding.
  - The UI shows non-secret metadata only (subject, issuer, thumbprint, validity window, hostname match). Secrets are never persisted.
  - Chain visibility: the table surfaces chain presence/count and whether the provided chain links to the leaf (linked/validated).
  - Rotate UX: the page requires confirmation and, after success, displays both the new and previous thumbprints and expirations inline.

Admin API endpoints (under `/admin/api`) — Development vs. Production

- Development: endpoints may be open for local testing.
- Non-Development: protected by the `AdminOnly` policy (Admin role or `admin.full` scope); send a valid access token.
  - CSRF: when enabled at the Gateway, send `X-Tansu-Csrf: <token>` with state‑changing calls (POST/DELETE). The Dashboard attaches this header automatically when configured.
  - Audit: all mutations emit structured audit events (`DomainBind`, `DomainBindPem`, `DomainRotate`, `DomainUnbind`).

Endpoints

- List bindings
  - GET `/admin/api/domains`
  - 200 OK → `[{ host, subject, issuer, thumbprint, notBefore, notAfter, hasPrivateKey, hostnameMatches, chainProvided, chainValidated, chainCount }]`

- Bind or replace using PFX
  - POST `/admin/api/domains`
  - Body (JSON): `{ "host": "example.com", "pfxBase64": "...", "pfxPassword": "optional" }`
  - Response: Certificate metadata for the active binding.

- Bind or replace using PEM
  - POST `/admin/api/domains/pem`
  - Body (JSON): `{ "host": "example.com", "certPem": "-----BEGIN CERTIFICATE-----...", "keyPem": "-----BEGIN PRIVATE KEY-----...", "chainPem": "-----BEGIN CERTIFICATE-----..." }`
  - `chainPem` is optional — supply intermediate(s) to complete the chain. Multiple PEM blocks are supported.
  - Response: Certificate metadata for the active binding.

- Rotate (returns previous/current)
  - POST `/admin/api/domains/rotate`
  - Body (JSON):
    - PFX form: `{ "host": "example.com", "pfxBase64": "...", "pfxPassword": "optional" }`
    - PEM form: `{ "host": "example.com", "certPem": "...", "keyPem": "...", "chainPem": "optional" }`
  - Response: `{ current: <metadata>, previous: <metadata or null> }`

- Delete a binding
  - DELETE `/admin/api/domains/{host}`
  - 204 No Content when removed; 404 Not Found when missing (idempotent).

Validation and behavior

- Certificates are validated on upload (parsable, has private key, not before/after sanity, hostname match against SAN/CN).
- Metadata includes a `hostnameMatches` flag to highlight mismatches (e.g., host doesn’t appear in SANs).
- Only non-secret metadata is stored in-memory to drive UI and auditing; certificate materials are not persisted.

CSRF protection

- When the Gateway is configured with a CSRF secret (`Gateway:Admin:Csrf`), POST endpoints require header `X-Tansu-Csrf`.
- The Dashboard admin UI provides an optional CSRF field on the Domains page. You can also set `DASHBOARD_CSRF` in the Dashboard environment to attach this header automatically.

Auditing

- Admin actions (bind, rotate, delete) create audit entries in the `Gateway.Admin` channel with non-secret summaries (host, thumbprint, validity window, hostname match).
- In development without Postgres, audit writes may log transient connection errors; this does not affect the admin operation itself.

## 7. Configuration & Secrets

- Configuration sources (appsettings, environment, user secrets)
- Sensitive values (client secrets, cert passwords)

### 7.1 Audit logging configuration (Task 31 Phase 1)

Audit logging is enabled in each service via the `Audit` configuration section. Defaults are safe for development; set explicit values in production.

Keys (per service appsettings/environment)

- `Audit:ConnectionString` — PostgreSQL connection string to the audit database (e.g., `Host=postgres;Port=5432;Database=tansu_audit;Username=postgres;Password=...`).
- `Audit:Table` — Target table name, default `audit_events`.
- `Audit:ChannelCapacity` — In-memory queue capacity for non-blocking writes, default `10000`.
- `Audit:FullDropEnabled` — When the channel is full, drop new events instead of blocking request path, default `true`.
- `Audit:MaxDetailsBytes` — Maximum serialized JSON size for `Details` payloads, default `16384` bytes. Larger payloads are truncated with a marker.
- `Audit:ClientIpHashSalt` — Optional string; when set, client IPs are pseudonymized using HMAC-SHA256 with this salt.

Operational notes

- The background writer creates the `audit_events` table and indexes at startup if missing (dev-friendly). An EF migration will be introduced in a later phase for long-term governance.
- Metrics are published under the `TansuCloud.Audit` meter: `audit_enqueued`, `audit_dropped`, and `audit_backlog`. Use SigNoz to observe ingestion health and backlog pressure.
- The enqueue path is non-blocking; if the channel is saturated, events are dropped and `audit_dropped` increments. Size budgets on `Details` ensure inserts remain efficient.
- Example environment variables and key paths

### 7.2 Audit UI, Export, and Retention (Task 31 Phase 3)

Admin Audit page

- Navigate to `/dashboard/admin/audit` to view the central audit trail.
- Use the filter panel to narrow by time range, tenant, subject, category, action, service, outcome, correlation id, and "Impersonation only".
- Click a row to see details and correlation identifiers. A quick link to SigNoz is provided for deeper trace/log analysis.

Export (admin-only)

- CSV: `GET /db/api/audit/export/csv?startUtc=...&endUtc=...&tenantId=...&...`
- JSON: `GET /db/api/audit/export/json?startUtc=...&endUtc=...&tenantId=...&...`
- Access control: requires an access token with `admin.full` scope.
- Limits: server enforces an upper bound of 10,000 rows per export. You can pass `limit=NNN` to request fewer rows.
- The export action itself is audited with an allowlisted summary (kind, count, filters).

Retention (Database service)

- Configuration section: `AuditRetention`
  - `Days` (int, default 180): rows older than `now - Days` are removed (or redacted if configured).
  - `LegalHoldTenants` (string[]): tenants exempt from deletion/redaction.
  - `RedactInsteadOfDelete` (bool, default false): when true, `details` is nulled and outcome/reason are marked instead of deleting rows.
  - `Schedule` (TimeSpan, default 6:00:00): execution interval of the background job.
- The retention worker emits an audit event with the number of affected rows and the cutoff.

Notes

- All audit Details are already redacted at write-time via the SDK; exports do not include raw PII.
- In Development, non-admin users can still query their own tenant’s audit events via `/db/api/audit` when providing `X-Tansu-Tenant`.

### 7.3 Identity security operations (Task 6)

Task 6 introduced several security controls that operators can manage without redeploying Identity. Use the admin APIs or Dashboard pages below to keep accounts protected while maintaining an auditable trail.

#### Multifactor authentication (MFA) policy switches

- UI: `/dashboard/admin/security` → **Sign-in policy** card.
- API: `PUT /identity/admin/api/security/policy` with body `{"requireMfa": true}` (additional fields will be preserved).
- Configuration baseline: `IdentityPolicy:RequireMfa` defaults to `false` in development but is intended to be switched on for production tenants via the admin UX/API.
- Behavior:
  - When enabled, the token issuance pipeline (`TokenClaimsHandler`) blocks new access tokens for users lacking a confirmed second factor.
  - Admins must enforce at least one MFA method (Authenticator app or SMS) per user before toggling `RequireMfa` to avoid issuance failures.
- Auditing: every change is logged in the `Identity.Security` audit channel with the actor, previous value, new value, and optional reason string.

#### JWKS rotation workflow

- UI: `/dashboard/admin/security/keys` shows active/previous keys and next scheduled rotation.
- API: `POST /identity/admin/api/security/keys/rotate` triggers an on-demand rotation; schedule is controlled by `IdentityPolicy:JwksRotationPeriod` (default 30 days).
- Background job: `JwksRotationService` evaluates the schedule on startup and thereafter at the configured cadence, retiring keys after the grace period.
- Operational guidance:
  - Prefer on-demand rotation before deploying new signing certificates or when a key compromise is suspected.
  - Retired keys remain available (with `IsCurrent = false`) until after the `RetireAfter` window, ensuring existing tokens continue to validate.
- Auditing: manual rotations and automatic retirements both emit audit entries identifying the previous/current key IDs.

#### External OIDC provider management per tenant

- UI: `/dashboard/admin/external-providers` lists providers, including per-tenant overrides.
- API surface: `GET/POST/PATCH/DELETE /identity/admin/api/external-providers`.
- Supported fields: `tenantId`, `displayName`, `clientId`, `clientSecret`, `authority`, `scopes`, and `enabled` flag. Omit `tenantId` for global defaults.
- Lifecycle:
  1. Create the provider record (disabled by default) with client credentials and scopes.
  2. Toggle **Enabled** once the upstream issuer is reachable; the registration service wires it into ASP.NET Identity sign-in automatically at runtime.
  3. Use tenant overrides to point different tenants to distinct upstream IdPs (for example, corporate Azure AD per customer).
- Security: secrets are stored encrypted in the configuration store; audit entries capture create/update/delete operations and reveal only masked client secrets.

#### Admin impersonation controls

- UI: `/dashboard/admin/impersonation` allows privileged operators to impersonate a tenant user for troubleshooting.
- API: `POST /identity/admin/api/impersonation/start` with payload `{ "userId": "..." }`; end with `POST /identity/admin/api/impersonation/end`.
- Safeguards:
  - Only users with the `admin.full` scope and `Administrator` role may impersonate.
  - Generated tokens are short-lived and stamped with `impersonated=true` plus the acting admin’s identity.
- Auditing: both start and end actions produce entries in the `Identity.Security` channel including the impersonated user, actor, and correlation id. Downstream services can detect the `impersonated` claim to show banners or restrict sensitive actions.

#### Monitoring and troubleshooting

- Audit the above actions via `/dashboard/admin/audit` using filters `Category = Identity.Security` or `Action` values `PolicyChanged`, `JwksRotated`, `ExternalProviderUpdated`, and `ImpersonationStarted/Ended`.
- Background services (`JwksRotationService`, `AuditBackgroundWriter`) log transient database connection warnings in development when PostgreSQL isn’t running; these do not indicate feature failure if the operation itself succeeds.
- For production, ensure `Audit:ConnectionString` points to the shared audit database so that security events persist even during rotations or impersonation sessions.

## 8. Health & Monitoring

- Health endpoints and readiness gates
- OpenTelemetry signals (traces, metrics, logs)
- Collector endpoint and backends
- Infrastructure telemetry (PostgreSQL, PgCat, Garnet)

### 8.1 Caching (HybridCache) overview

- Services use Microsoft.Extensions.Caching.Hybrid with Redis as the distributed backing store when configured.
- Development toggle `Cache:Disable=1` (per service) bypasses HybridCache entirely for troubleshooting.
- Redis connectivity is covered by custom health checks ("redis" tag) that ping the configured endpoint; readiness will report degraded when Redis is unreachable.
- Gateway OutputCache: Anonymous responses may be cached briefly (default TTL 15s, configurable via `Gateway:OutputCache:DefaultTtlSeconds`). Requests with `Authorization` are not cached at the Gateway.
- Cache keys are tenant-scoped and versioned: `t:{tenant}:v{version}:{service}:{resource}:...`.
  - Database and Storage invalidate via outbox events (see Task 12 and Task 15). Storage also bumps the tenant cache version on local PUT/DELETE for immediate invalidation.
  - Storage bumps a per-tenant version on PUT/DELETE to invalidate list/head entries.
- Metrics: the Storage service exposes counters via the `tansu.storage` meter:
  - `tansu_storage_cache_attempts_total`
  - `tansu_storage_cache_hits_total`
  - `tansu_storage_cache_misses_total`
  Each counter includes an `op` tag (list/head). Exporters can aggregate/graph these for hit ratio.

### 8.2 SigNoz UI configuration (Task 40 compliant)

To keep URLs consistent across environments and satisfy Task 40 (No New Hardcoded URLs), the Dashboard derives the SigNoz UI base URL from configuration. Prefer setting an explicit base when the SigNoz UI is reachable to operators.

- Preferred key: `SigNoz:BaseUrl`
  - Environment variables are supported as `SigNoz__BaseUrl` (double underscore) or `SIG_NOZ_BASE_URL`.
  - Example (dev): `http://127.0.0.1:3301/`.
  - Example (prod): `https://observability.example.com/`.

- Fallback behavior (dev convenience only):
  - If `SigNoz:BaseUrl` is not configured, the Dashboard will derive the SigNoz base from the host component of `PublicBaseUrl` (preferred) or `GatewayBaseUrl` by combining `http(s)://{host}:3301/`.
  - This ensures there are no hardcoded literals in app code, and the UI links remain consistent with the environment.

- Task 40 compliance:
  - No hardcoded `http://localhost:3301` or similar literals are introduced. All external links are composed from configured base URLs.
  - Tests and docs may show loopback examples where they are covered by the repository allowlist; runtime code does not inline such literals.

Notes

- The Dashboard metrics page no longer embeds charts directly; it shows curated links to SigNoz dashboards. Ensure the SigNoz UI is reachable from operator networks.
- When running via Docker Compose in development, SigNoz UI typically binds to port 3301 on the host.

Troubleshooting tips

- To temporarily disable caching in Development, set `Cache:Disable=1` on the affected service (e.g., Storage) and restart it.
- Verify Garnet availability: check `/health/ready` and service logs for the Garnet/Redis ping health check. In Compose, ensure the `redis` service (Garnet) is up.
- If responses seem stale after writes, confirm that the tenant cache version increments (Storage) or that outbox events flow (Database).

### 8.3 Infrastructure telemetry (PostgreSQL, PgCat, Garnet)

Overview

- All infrastructure services now export metrics to the OTLP collector via Prometheus exporters, providing unified observability across the entire stack in SigNoz.
- This enables correlation between application traces and infrastructure state (e.g., slow DB queries, connection pool saturation, Garnet memory pressure).

Services and exporters

1. **PostgreSQL** (`postgres-exporter`)
   - Image: `quay.io/prometheuscommunity/postgres-exporter:latest`
   - Port: 9187
   - Metrics: connection pool stats, query performance, table/index sizes, replication lag, transaction rates, cache hit ratios
   - Configuration: `DATA_SOURCE_NAME` points to the main PostgreSQL instance with monitoring credentials

2. **PgCat** (`pgcat-exporter`)
   - Port: 9188
   - Metrics: active connections per pool, queued clients, pool saturation, backend health, query routing decisions
   - Essential for detecting connection exhaustion and routing bottlenecks

3. **Garnet** (`redis-exporter`)
   - Image: `oliver006/redis_exporter:latest` (compatible with Garnet's Redis protocol)
   - Port: 9121
   - Metrics: memory usage, eviction counts, command latency, keyspace stats, replication lag, hit/miss ratios
   - Configuration: `REDIS_ADDR=redis:6379` (service name remains "redis" for backwards compatibility)

OTLP collector integration

- The collector scrapes all three exporters every 30 seconds via the Prometheus receiver
- Metrics flow: Prometheus receiver → Batch processor → OTLP exporter → SigNoz → ClickHouse
- Scrape jobs configured in `dev/signoz-otel-collector-config.yaml` under `receivers.prometheus.config.scrape_configs`

Verification (development)

```powershell
# Start the stack
docker compose up -d

# Wait for services to be healthy (~60 seconds)

# Verify exporters are running
docker ps | grep exporter

# Check metrics endpoints
curl http://127.0.0.1:9187/metrics  # PostgreSQL
curl http://127.0.0.1:9188/metrics  # PgCat
curl http://127.0.0.1:9121/metrics  # Garnet (via redis-exporter)

# Check collector logs
docker logs signoz-otel-collector | grep -E "postgres|pgcat|redis"

# Open SigNoz UI
Start-Process http://127.0.0.1:3301/
```

Production considerations

- **Version pinning**: Use specific exporter versions (e.g., `postgres-exporter:v0.15.0`) instead of `:latest` to ensure stability
- **Scrape intervals**: Adjust to 60s or 120s for larger deployments to reduce load
- **Authentication**: Exporters should not be exposed outside the Docker network; keep them internal-only
- **Resource limits**: Set memory/CPU limits on exporter containers to prevent resource exhaustion
- **Monitoring credentials**: Use read-only database users for PostgreSQL exporter with minimal privileges

Recommended SigNoz alerts

- **PostgreSQL**:
  - Connection pool > 80% capacity
  - Replication lag > 10 seconds
  - Cache hit ratio < 90%
  - Long-running queries > 30 seconds

- **PgCat**:
  - Queued clients > 10
  - Pool saturation > 90%
  - Backend health check failures > 3 in 5 minutes

- **Garnet** (Redis-compatible):
  - Memory usage > 80% of max
  - Eviction rate > 100/minute
  - Command latency p95 > 10ms
  - Connection failures > 5 in 5 minutes

Troubleshooting

- **Exporter not starting**: Check `docker logs <exporter-container>` for connection errors or credential issues
- **Metrics not appearing in SigNoz**: Verify collector scrape configuration and check collector logs for scrape errors
- **Stale metrics**: Ensure exporter health checks are passing; restart the exporter if needed
- **High cardinality**: Some exporters can produce high-cardinality metrics (e.g., per-query stats); review SigNoz retention settings

For detailed implementation notes and acceptance criteria, see Task 08 in `Tasks-M1.md`.

## 9. Operations

- Rolling updates and restarts
- Log collection and troubleshooting
- Backup and restore considerations (DB/storage specifics TBD)

### 9.1 Product telemetry reporting (Dashboard → main server)

What it is

- The Dashboard captures Warning+ logs locally and, by default, reports a minimal, privacy-preserving subset to a central “main server” hourly. This helps improve the product without exposing tenant data.

What leaves your deployment (defaults)

- Errors and Critical logs relevant to product health.
- Selected Warnings from allowlisted categories (Identity/OIDC, Gateway proxy, Storage/Database dependencies, security boundary). Other Warnings are sampled (default 10%).
- Aggregated performance SLO breaches as summary items (counts/percentiles), not per-request details.
- Coarse context only: service name, environment, version, timestamp, optional normalized route/operation, optional status class, optional exception type, and a hash of the message template.
- No PII or payloads: no raw URLs, query strings, headers, bodies, cookies, user IDs/emails, object keys/paths.

Configuration (Dashboard)

- App settings section: `LogReporting` (binds to options in code).
  - `Enabled` (bool, default true): master switch for periodic reports.
  - `ReportIntervalMinutes` (int, default 60): how often to send.
  - `MainServerUrl` (string): base URL of your main server; reports POST to `<MainServerUrl>/api/logs/report`.
  - `ApiKey` (string, optional): bearer token for the report request.
  - `SeverityThreshold` (string, default `Warning`): minimum level to consider.
  - `QueryWindowMinutes` (int, default 60): time window to include.
  - `MaxItems` (int, default 2000): cap per report.
  - `HttpTimeoutSeconds` (int, default 30): outbound HTTP timeout.
  - `WarningSamplingPercent` (int, default 10): sampling for non-allowlisted warnings.
  - `AllowedWarningCategories` (string[], defaults include `OIDC-`, `Tansu.Gateway`, `Tansu.Storage`, `Tansu.Database`, `Tansu.Identity`).
  - `HttpLatencyP95Ms` (int, default 500), `DbDurationP95Ms` (int, default 300), `ErrorRatePercent` (int, default 1) for aggregated perf events.
  - `PseudonymizeTenants` (bool, default true) and `TenantHashSecret` (string, optional) control HMAC hashing of tenant identifiers.
- Environment variables follow the standard double-underscore convention (derive from option names). Prefer configuring them via `.env` so compose and VS Code tasks stay aligned. Common keys:

  | Environment key | Description | Default (Development) |
  | --- | --- | --- |
  | `LogReporting__Enabled` | Master switch for the background reporter | `false` |
  | `LogReporting__MainServerUrl` | Base URL for the central ingestion endpoint | empty |
  | `LogReporting__ApiKey` | Bearer token attached to outbound reports | empty |
  | `LogReporting__ReportIntervalMinutes` | Minutes between report attempts | `60` |
  | `LogReporting__SeverityThreshold` | Minimum log level considered for export | `Warning` |
  | `LogReporting__QueryWindowMinutes` | Lookback window for buffered logs | `60` |
  | `LogReporting__MaxItems` | Maximum entries per report | `2000` |
  | `LogReporting__HttpTimeoutSeconds` | HTTP timeout when calling the main server | `30` |
  | `LogReporting__WarningSamplingPercent` | Sampling rate for non-allowlisted warnings | `10` |
  | `LogReporting__AllowedWarningCategories__0..n` | Allowlist prefixes for product warnings | `OIDC-`, `Tansu.Gateway`, `Tansu.Storage`, `Tansu.Database`, `Tansu.Identity` |
  | `LogReporting__HttpLatencyP95Ms` / `LogReporting__DbDurationP95Ms` / `LogReporting__ErrorRatePercent` | Perf SLO breach thresholds | `500` / `300` / `1` |
  | `LogReporting__PseudonymizeTenants` | Enable tenant hash anonymization | `false` |
  | `LogReporting__TenantHashSecret` | Optional HMAC secret for tenant hashes | empty |

Runtime toggle

- Admins can disable or re-enable reporting on the fly at Dashboard → Admin → Logs. This does not affect local log capture/viewing.
- The Admin Logs card now surfaces the effective status (`Configured`, `Runtime`, `Effective`), the active endpoint, sampling percentage, warning allowlist, pseudonymization flags, and the set of report kinds (`critical`, `error`, `warning` (allowlisted/sampled), `perf_slo_breach`, `telemetry_internal`). This makes it clear what will leave the deployment.
- Use the **Send test report** button to emit a harmless `diagnostic_heartbeat` envelope on demand. The call uses the currently bound `LogReporting` options, and the UI confirms the target endpoint when the POST succeeds. This is the fastest way to validate egress connectivity after configuring credentials.

Buffering and failure handling

- Logs are first captured into a bounded in-memory buffer (default 5,000 entries) for the Admin Logs page.
- The reporter snapshots the buffer, filters items for reporting, sends them, and only then dequeues the sent items. If the send fails (non-2xx or network error), nothing is removed and the next cycle retries. This avoids data loss.

Security notes

- Use HTTPS for `MainServerUrl` in production and set a strong `ApiKey`.
- Ensure the main server’s ingestion endpoint validates the bearer token and rate-limits as needed.

Troubleshooting

- Reporting disabled: set `LogReporting:Enabled=true` and verify `MainServerUrl` is set.
- Connectivity/auth: check Dashboard logs for "Log report failed" messages and confirm `ApiKey` is valid.
- Volume too high: raise `SeverityThreshold`, lower `WarningSamplingPercent`, or tighten `AllowedWarningCategories`.
- Test report fails: confirm the Admin Logs UI shows the correct endpoint, verify DNS/TLS reachability to `<MainServerUrl>`, and ensure the `ApiKey` grants access. The API returns a 400 Problem when the endpoint is missing, so admins can fix configuration without digging through logs.
- Tenant hashes look unexpected: set `LogReporting__PseudonymizeTenants=false` (or clear the hash secret) for diagnostics, but revert to pseudonymized mode for production deployments.

Validation checklist (after configuring telemetry)

1. Confirm `MainServerUrl` and `ApiKey` are populated via configuration (`appsettings.{Environment}.json`, `.env`, or host secrets).
2. In Dashboard → Admin → Logs, check that `Configured`, `Runtime`, and `Effective` read `true` when telemetry should flow.
3. Review the status details: warning sampling percentage, allowlisted categories, pseudonymization flags, and report kinds.
4. Click **Send test report** and ensure the success toast reports the target endpoint. If it fails, inspect the returned error details and Dashboard logs.
5. (Optional) Monitor the main server ingress endpoint for the `diagnostic_heartbeat` entry to ensure end-to-end flow works.

Durable sink posture

- SigNoz (ClickHouse) remains the authoritative sink for tenant-visible logs, traces, and metrics. Product telemetry is the only egress to Tansu Cloud and is limited to the policy above. Toggle reporting off at runtime or via `LogReporting__Enabled=false` if a deployment must stay fully air-gapped. We intentionally keep product telemetry separate from SigNoz to avoid double ingestion; operators who want a local copy can add an OTEL processor/receiver alongside SigNoz, but that remains optional and off by default.

### 9.2 Telemetry ingestion service (`telemetry.tansu.cloud`)

What it is

- Dedicated minimal API (`TansuCloud.Telemetry`) that receives the Dashboard reporter batches at `POST /api/logs/report`, persists envelopes to SQLite, and now ships a Razor-based admin console at `/admin` for triage, acknowledgement, and archiving.
- Envelope identifiers are issued as UUID v7 values to preserve chronological ordering and compatibility with downstream analytics. Expect the admin console and export payloads to surface these sortable identifiers.
- Runs outside the main cluster so product telemetry can continue operating even when tenant infrastructure is offline or being upgraded.

Development note

- In local docker-compose runs the telemetry admin console is exposed directly on <http://127.0.0.1:5279>. Add `Authorization: Bearer $env:TELEMETRY__ADMIN__APIKEY` when testing with a browser plugin or CLI. The gateway deliberately does **not** proxy `/telemetry/*`; keep using the direct port (or your own hardened reverse proxy in production) so auth headers stay under your control.
- Running the project directly via `dotnet run` or Visual Studio binds the service to <http://127.0.0.1:5279> (and `http://localhost:5279`) by default. Set `ASPNETCORE_URLS` or pass `--urls` if you need a different port for ad-hoc diagnostics; Docker/compose scenarios continue to listen on `http://0.0.0.0:8080` inside the container.
- Ensure `.env` (or your secret store) includes `TELEMETRY__ADMIN__APIKEY` before starting compose or calling the helper script; both the admin console login flow and CLI helpers require that key.
- If `/` or `/admin` responds with the health payload instead of the login screen, restart the service to ensure the latest build is running and double-check that no prior `dotnet run` instance is still bound to port 5279. The canonical routing order keeps `app.MapRazorPages()` before `MapHealthChecks()`. If you recently merged code, rebuild the solution so the updated middleware order is picked up before retrying the UI.

Deployment topology

- Host the container on a hardened VM or managed container host with a public DNS record `telemetry.tansu.cloud` pointing to your reverse proxy (e.g., Nginx/Traefik/Caddy) and a valid TLS certificate. We require HTTPS for all traffic.
- Expose only TCP/443 externally; block all other inbound ports. The reverse proxy forwards `https://telemetry.tansu.cloud/api/*` to the container’s HTTP port (default 8080).
- Persist the SQLite database on encrypted disk. The default compose volume path is `/var/opt/tansu/telemetry`; ensure the directory exists and is backed up on the host (nightly copy or snapshot).
- Recommended compose snippet (adjust volume paths and secrets):

  ```yaml
  telemetry:
    image: ghcr.io/tansucloud/telemetry:latest
    restart: unless-stopped
    environment:
      ASPNETCORE_URLS: http://+:8080
      Telemetry__Ingestion__ApiKey: "${TELEMETRY__INGESTION__APIKEY}"
      Telemetry__Admin__ApiKey: "${TELEMETRY__ADMIN__APIKEY}"
      Telemetry__Database__FilePath: /var/opt/tansu/telemetry/telemetry.db
      Telemetry__Database__EnforceForeignKeys: true
      OpenTelemetry__Otlp__Endpoint: http://signoz-otel-collector:4317 # optional, omit if SigNoz not reachable
    volumes:
      - /var/opt/tansu/telemetry:/var/opt/tansu/telemetry
    networks:
      - telemetry-backend
  ```

#### Admin console and authentication flow

- The admin console lives at `/admin` (aliases `/admin/envelopes` and detail pages under `/admin/envelopes/{id}`) and shares the same bearer authentication scheme as the JSON admin API. Every request must include `Authorization: Bearer <Telemetry__Admin__ApiKey>`.
- Browser workflow: navigating to `/admin` without an Authorization header now redirects to `/admin/login` with a guided prompt. Paste the current admin API key into the form to mint a short-lived, HttpOnly session cookie. When the key rotates or the cookie is cleared, the login page surfaces a clear status banner (for example, “session expired — enter the current key”).
- Because standard browsers cannot add custom Authorization headers, terminate traffic behind a reverse proxy that injects the header for trusted operators. Example snippets:
  - **Nginx**

    ```nginx
    location /admin/ {
        proxy_set_header Authorization "Bearer $telemetry_admin_key";
        proxy_pass http://telemetry:8080/admin/;
    }
    ```

  - **Traefik** (static middleware)

    ```toml
    [http.middlewares.telemetry-admin.headers.customRequestHeaders]
    Authorization = "Bearer ${TELEMETRY_ADMIN_KEY}"
    ```

  Apply the middleware/headers only on the admin path and scope access via corporate VPN or allowlisted source IPs. Never embed the raw key in client-side code.
- For command-line validation, run `curl -H "Authorization: Bearer $TELEMETRY__ADMIN__APIKEY" https://telemetry.tansu.cloud/api/admin/envelopes?page=1` to ensure the proxy is forwarding the header correctly before exposing the UI.
- PowerShell operators can use the helper script `dev/tools/call-telemetry-admin.ps1` to exercise the admin API without crafting headers manually. The script loads `.env` via `Import-TansuDotEnv`, reads `TELEMETRY__ADMIN__APIKEY`, and targets `TELEMETRY__DIRECT__BASEURL` (defaults to `http://127.0.0.1:5279` if unset). Example:

  ```pwsh
  pwsh ./dev/tools/call-telemetry-admin.ps1 -Method Get -Path '/api/admin/envelopes?page=1'
  ```

  Provide `-Body` for POST/PUT calls or `-Raw` to print the raw response. If the API key environment variable is missing, the script exits with an error so you can fix `.env`/secret store entries before retrying.
- Supported workflows today:
  - **Filters and metrics** — the list view provides quick filters (service, environment, host, severity floor, time range, acknowledgement/archive flags, free-text search) plus on-page counters (active/archived/acknowledged envelopes and total item count).
  - **Detail drilling** — click an envelope to inspect log items with structured properties, correlation IDs, and exception payloads ordered newest-first.
  - **Lifecycle controls** — acknowledge or archive directly from either the list or detail page; status toasts confirm actions and the table disables buttons once state flips.
  - **Pagination guardrails** — UI enforces `Telemetry__Admin__DefaultPageSize`/`MaxPageSize`; invalid combinations fall back gracefully with validation messaging.
  - **Ingestion health at a glance** — the header surfaces queue depth, capacity, and utilisation so operators can spot backlog pressure quickly.
  - **Filtered exports** — download CSV or JSON for the active filter set via the header buttons; downloads include item windows and respect the configured export cap. Automation can continue to call `/api/admin/envelopes/export/{json|csv}` with the admin API key.

Configuration keys

| Environment key | Description | Notes |
| --- | --- | --- |
| `Telemetry__Ingestion__ApiKey` | Bearer token required by Dashboard reporters. Rotate when compromised. | Set the same value in Dashboard (`LogReporting__ApiKey`). |
| `Telemetry__Admin__ApiKey` | API key for `/api/admin/*` and the Razor admin console. | Store separately from ingestion key; grant to internal operators only. |
| `Telemetry__Admin__DefaultPageSize` | Default page size applied when operators first load the console. | Must be between 1 and 500; defaults to `50`. |
| `Telemetry__Admin__MaxPageSize` | Upper bound enforced on page size selections. | Prevents expensive queries; defaults to `200`. |
| `Telemetry__Admin__MaxExportItems` | Caps CSV/JSON export size for UI and API downloads. | Defaults to `500`; lower it if downstream tooling struggles with large payloads. |
| `Telemetry__Database__FilePath` | Absolute or relative path to the SQLite file. | Defaults to `App_Data/telemetry/telemetry.db` inside the container; override to place on mounted storage. |
| `Telemetry__Database__EnforceForeignKeys` | Enables SQLite FK enforcement. | Keep `true` unless troubleshooting schema upgrades. |
| `Telemetry__Ingestion__QueueCapacity` | Optional override for in-memory queue depth. | Increase cautiously; defaults protect memory usage. |

Operational checklist

1. Provision API keys and store them in your secret manager (Azure Key Vault, AWS Secrets Manager, etc.). Update the container environment without baking keys into images.
1. Verify health endpoints:
   - `GET /health/live` responds 200 once the process starts.
   - `GET /health/ready` gates on SQLite availability and ingestion queue readiness. Instrument your proxy or load balancer to watch this endpoint.
1. Confirm ingestion by running Dashboard → Admin → Logs → **Send test report**; the telemetry logs should show "Accepted telemetry payload" entries.
1. Open `/admin` through the proxy and confirm the list view renders with the expected counts. Use the filters to prove that acknowledgement and archive toggles respect the configuration limits.
1. Review `/api/admin/envelopes?page=1` using the admin API key as a secondary validation (JSON payload mirrors the UI results and is useful for scripts).
1. Trigger a CSV or JSON export from the UI (or hit `/api/admin/envelopes/export/json`) to confirm downloads respect filters and the configured export cap.
1. Schedule filesystem backups for the SQLite path (retain at least 30 days). Optionally mirror data into ClickHouse or Postgres for long-term analytics—Document the downstream pipeline if you add one.

Security posture

- TLS termination must enforce TLS 1.2 or newer. Issue certificates via ACME automation (Let’s Encrypt/ZeroSSL) and rotate automatically.
- Put the telemetry service behind a WAF/rate limiter. Suggested baseline: 60 requests/min per source IP, 10 concurrent connections.
- Keep the admin API scope-restricted by IP allowlist (e.g., corporate VPN) or additional auth (mTLS or forward auth provider).
- Rotate API keys quarterly and whenever someone leaves the operations team. The Dashboard configuration accepts hot swaps without restarts.

Integration with Dashboard

- Update `LogReporting__MainServerUrl` (in `.env`, environment variables, or appsettings) to `https://telemetry.tansu.cloud/api/logs/report`.
- Align `LogReporting__ApiKey` with the ingestion key above. Restart the Dashboard only if configuration providers require it; `.env` + compose reload picks it up automatically.
- Maintain parity between dev/staging/production: dev compose already points to the local telemetry container and uses the values in `.env` (`TELEMETRY__INGESTION__APIKEY`, `TELEMETRY__ADMIN__APIKEY`).

### 9.3 Database container upgrades and PostgreSQL extension management

#### The challenge

TansuCloud uses a custom PostgreSQL image (`tansu/citus-pgvector`) that includes:

- **Citus** — distributed PostgreSQL extension for multi-tenant sharding
- **pgvector** — vector similarity search for embeddings (ML workloads)

When you rebuild or upgrade this base image (e.g., moving from Citus 13.1 to 13.2, or pgvector 0.7 to 0.8), the **shared libraries** inside the container update immediately. However, the **extension metadata** in existing tenant databases remains at the old version until explicitly upgraded via SQL.

This version mismatch triggers PostgreSQL error `XX000`:

```
loaded Citus library version differs from installed extension version
Hint: Run ALTER EXTENSION citus UPDATE and try again.
```

**Impact**: All database writes fail with `500 InternalServerError` until extensions are updated. This can cause production downtime if not handled proactively.

#### Production upgrade strategy

##### 1. Pin specific versions (mandatory for production)

**Development** uses `:latest` tags for convenience, but **production must pin explicit versions**:

```dockerfile
# dev/Dockerfile.citus-pgvector (production variant)
FROM citusdata/citus:12.1-pg16  # Pin major.minor.patch
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
      postgresql-16-pgvector=0.8.0-1 && \  # Pin exact version
    rm -rf /var/lib/apt/lists/*
```

Tag images with the Citus+pgvector version:

```bash
docker build -f dev/Dockerfile.citus-pgvector \
  -t yourorg/citus-pgvector:citus12.1-pg16-pgvector0.8.0 .
docker push yourorg/citus-pgvector:citus12.1-pg16-pgvector0.8.0
```

Update `docker-compose.prod.yml` with the pinned tag:

```yaml
services:
  postgres:
    image: yourorg/citus-pgvector:citus12.1-pg16-pgvector0.8.0
```

**Rationale**: Pinning prevents surprise upgrades. You upgrade deliberately after testing, not automatically when pulling `:latest`.

##### 2. Pre-flight extension checks (automatic safety net)

> ✅ **Implemented**: The TansuCloud Database service includes automatic extension pre-flight checks on startup via `ExtensionVersionService` and `ExtensionVersionHostedService`. This feature is enabled by default in all environments.

**What it does**:

- Automatically discovers all tenant databases (`tansu_tenant_*`)
- Runs `ALTER EXTENSION citus UPDATE` and `ALTER EXTENSION vector UPDATE` on each database
- Logs version transitions (e.g., `vector 0.8.0 → 0.8.1`)
- Fails startup in production if updates fail (prevents partial upgrades)
- Can be disabled via `SKIP_EXTENSION_UPDATE=true` environment variable if needed

**Startup logs** (example):

```
info: ExtensionVersionHostedService[0]
      Running pre-flight extension version checks...
info: ExtensionVersionService[0]
      Found 7 tenant database(s) to check
info: ExtensionVersionService[0]
      [tansu_tenant_acme_dev] Updated extension vector from 0.8.0 to 0.8.1
info: ExtensionVersionService[0]
      Pre-flight extension checks completed. Processed 7 database(s)
```

**Health check integration**:

- Extension versions are reported at `/db/health/ready` under the `extension_versions` check
- Example response:

  ```json
  {
    "extension_versions": {
      "status": "Healthy",
      "description": "All 7 tenant database(s) have consistent extension versions",
      "data": {
        "databases": 7,
        "extensions": ["citus", "vector"],
        "citus_version": "13.2-1",
        "vector_version": "0.8.1"
      }
    }
  }
  ```

**Audit logging**:

- All extension updates are logged to the audit table with action `database.extension.update`
- Details include: database name, extension name, old version, new version, timestamp
- Queryable for compliance and forensics

**Implementation details**: See `TansuCloud.Database/Services/ExtensionVersionService.cs` and `TansuCloud.Database/Hosting/ExtensionVersionHostedService.cs`.

**When to disable**: Use `SKIP_EXTENSION_UPDATE=true` only for troubleshooting. In production, keep this enabled to prevent XX000 errors after image upgrades.

##### 3. Blue-green deployment pattern (zero-downtime upgrades)

For large production deployments, use a blue-green strategy:

**Before upgrade:**

1. **Current (blue) stack**: Running `citus12.1-pg16-pgvector0.7.0`
2. **New (green) stack**: Prepare `citus12.1-pg16-pgvector0.8.0` image

**Upgrade steps:**

```bash
# 1. Tag current production stack as "blue"
docker tag yourorg/citus-pgvector:production yourorg/citus-pgvector:blue

# 2. Deploy green stack in parallel (different ports/network)
docker-compose -f docker-compose.green.yml up -d

# 3. Run extension updates on green databases
docker exec tansu-postgres-green psql -U postgres <<EOF
DO \$\$
DECLARE
    db text;
BEGIN
    FOR db IN SELECT datname FROM pg_database 
              WHERE datname LIKE 'tansu_tenant_%'
    LOOP
        EXECUTE 'ALTER EXTENSION citus UPDATE' 
            USING DATABASE = db;
        RAISE NOTICE 'Updated Citus in %', db;
    END LOOP;
END \$\$;
EOF

# 4. Smoke test green stack
curl -f http://green-gateway:8080/health/ready

# 5. Switch traffic (DNS, load balancer, or gateway config)
# Point public traffic to green stack

# 6. Monitor for 24-48 hours

# 7. Decommission blue stack
docker-compose -f docker-compose.blue.yml down
```

**Rollback**: If issues arise, revert DNS/LB to blue stack within seconds.

##### 4. Migration-based approach (integrated with EF Core)

For teams that prefer explicit migrations over runtime checks:

**Create a migration** that updates extensions:

```csharp
// TansuCloud.Database/EF/Migrations/20251007_UpdateCitusExtension.cs
public partial class UpdateCitusExtension : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER EXTENSION citus UPDATE;");
        migrationBuilder.Sql("ALTER EXTENSION vector UPDATE;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Extensions don't support downgrade; log warning
        migrationBuilder.Sql("SELECT 1; -- Cannot downgrade extensions");
    }
}
```

**Pros**: Version controlled, auditable, runs automatically during deployment.

**Cons**: Requires EF migration for every extension upgrade; may not catch drift if databases are provisioned outside migrations.

##### 5. Health checks for extension versions

> ✅ **Implemented**: The Database service includes a dedicated health check (`ExtensionVersionHealthCheck`) that reports current extension versions at the `/db/health/ready` endpoint.

**What it provides**:

- Reports extension versions across all tenant databases
- Detects version mismatches (unhealthy state)
- Exposes metrics for monitoring systems

**Example response** at `/db/health/ready`:

```json
{
  "extension_versions": {
    "status": "Healthy",
    "description": "All 7 tenant database(s) have consistent extension versions",
    "data": {
      "databases": 7,
      "extensions": ["citus", "vector"],
      "citus_version": "13.2-1",
      "vector_version": "0.8.1"
    }
  }
}
```

**Monitoring integration**:

- Prometheus can scrape this endpoint for alerting
- Status changes to `Degraded` or `Unhealthy` if version mismatches are detected
- Use tags filter: `GET /db/health/ready?tags=extensions`

**Implementation**: See `TansuCloud.Database/Services/ExtensionVersionHealthCheck.cs`.

#### Development workflow (current state)

> ℹ️ **Note**: With automatic pre-flight checks enabled, manual extension updates are no longer required after rebuilding the citus-pgvector image. The Database service handles this automatically on startup.

- Dev uses `citusdata/citus:latest` and rebuilds frequently
- After rebuilding `tansu/citus-pgvector:local`, the Database service automatically updates extensions on next startup
- **Optional manual validation** (if pre-flight checks are disabled):

  ```powershell
  # List all tenant databases
  docker exec tansu-postgres psql -U postgres -c "\l" | Select-String "tansu_tenant"
  
  # Check extension versions
  docker exec tansu-postgres psql -U postgres -d tansu_tenant_acme_dev -c "
      SELECT extname, extversion FROM pg_extension WHERE extname IN ('citus', 'vector');
  "
  
  # View pre-flight check logs
  docker logs tansu-db --tail 50 | Select-String "ExtensionVersion|pre-flight"
  ```

#### Recommended production policy

| Aspect | Development | Production |
| --- | --- | --- |
| **Image tags** | `:latest` (fast iteration) | Pinned versions (e.g., `citus12.1-pg16-pgvector0.8.0`) |
| **Upgrade trigger** | Manual rebuild when needed | Scheduled maintenance window (quarterly or as needed) |
| **Extension updates** | Manual `ALTER EXTENSION` after rebuild | Pre-flight checks in Database service OR blue-green deployment |
| **Validation** | E2E tests after update | Health checks + canary deployment + full E2E suite |
| **Rollback** | Rebuild previous image | Keep previous image tagged; redeploy if issues arise |
| **Testing** | Local compose stack | Staging environment mirrors production; test full upgrade path |
| **Documentation** | Update dev notes in repo | Maintain runbook with rollback steps, contact info, RTO/RPO |

#### Upgrade checklist (production)

Before starting:

- [ ] **Pin versions** in Dockerfile (no `:latest` tags)
- [ ] **Build and scan** new image: `docker build`, `trivy image`, `cosign sign`
- [ ] **Test in staging**: Deploy to staging, run full E2E suite, monitor for 24 hours
- [ ] **Document rollback**: Tag current production image as `rollback-YYYYMMDD`
- [ ] **Schedule maintenance window**: Off-peak hours, communicate to tenants

During upgrade:

- [ ] **Deploy new image** (blue-green or rolling update)
- [ ] **Run pre-flight checks**: Database service validates extension versions on startup
- [ ] **Verify health endpoints**: `/health/ready` returns 200, extension versions match expected
- [ ] **Smoke test**: Create test tenant, seed documents, run vector search queries
- [ ] **Monitor logs**: Watch for `XX000` errors, connection pool issues, query latency spikes

After upgrade:

- [ ] **Run E2E suite**: Validate all integration points work
- [ ] **Monitor metrics**: Database query latency, connection pool utilization, error rates
- [ ] **Soak test**: Leave running for 24-48 hours before decommissioning old stack
- [ ] **Document**: Update runbook with actual duration, issues encountered, lessons learned

Rollback triggers:

- Any `XX000` extension version errors in production logs
- Query latency P95 > 2x baseline for > 15 minutes
- Error rate > 1% for > 5 minutes
- Failed E2E tests for critical workflows (auth, storage, vector search)

**Rollback procedure**:

```bash
# 1. Revert to previous image
docker tag yourorg/citus-pgvector:rollback-20251001 yourorg/citus-pgvector:production
docker-compose -f docker-compose.prod.yml up -d postgres

# 2. Extensions auto-downgrade is not supported; may need to restore from backup
# (This is why thorough staging tests are critical!)

# 3. Restart dependent services
docker-compose restart pgcat db storage

# 4. Verify health
curl -f https://apps.example.com/health/ready
```

#### Future improvements

- **Automated extension detection**: Database service could query `pg_available_extensions` and compare installed vs available versions, logging warnings when drift is detected.
- **Tenant database inventory**: Maintain a registry of tenant databases in Redis/ClickHouse so pre-flight checks don't need to query `pg_database` on every startup.
- **CI/CD pipeline**: Automate image builds on Citus/pgvector releases, run matrix tests (all supported versions), publish to registry with SBOMs and signatures.
- **Observability**: Export extension version metrics to SigNoz so dashboards can alert on version drift across the fleet.

## 10. Security Hardening

- CORS at the gateway
- TLS everywhere and HSTS (optional)
- Rate limiting and body size limits at the gateway

### 10.1 Gateway caching and request limits

Output caching (gateway)

- Base policy varies by:
  - Host and path
  - Headers: `X-Tansu-Tenant`, `Accept`, `Accept-Encoding`
  - Selected query parameters depending on route
- TTL: Default 15s (configurable via `Gateway:OutputCache:DefaultTtlSeconds`). Suitable for anonymous/static responses; tune per environment.
- Authorization-aware:
  - Requests with an `Authorization` header are not cached at the gateway to avoid serving private data from cache.

OutputCache policy matrix

- Base policy (anonymous responses only)
  - TTL: `Gateway:OutputCache:DefaultTtlSeconds` (default 15 seconds)
  - Vary: `Host`, `X-Tansu-Tenant`, `Accept`, `Accept-Encoding`
- PublicStaticLong (static assets)
  - Routes: `/_framework/*`, `/dashboard/_framework/*`, `/_content/*`, `/dashboard/_content/*`, `/_blazor/*`, `/dashboard/_blazor/*`, `/favicon.ico`, `/app.css`, `/app.{hash}.css`, `/TansuCloud.Dashboard.styles.css`, `/TansuCloud.Dashboard.{hash}.styles.css`, and Identity/Dashboard assets addressed under `/lib/*`, `/css/*`, `/js/*`.
  - TTL: `Gateway:OutputCache:StaticTtlSeconds` (default 300 seconds)
  - Vary: `Host`, `Accept-Encoding`

Notes

- Authenticated requests bypass the OutputCache entirely at the gateway.
- Downstream services still emit `ETag` and `Vary` headers; client/proxy caches may leverage them even when gateway caching is bypassed.

Global rate limits

- Fixed window (default 10s, configurable via `Gateway:RateLimits:WindowSeconds`) with per-route-family limits; Identity has no queue.
- Partitioning to balance fairness and privacy:
  - Public (no `Authorization`): partition by tenant + client IP.
  - Authenticated: partition by tenant + hash(Authorization token) — hashed to avoid any sensitive value leakage.
- 429 responses include `Retry-After` equal to the configured window seconds to hint clients when to retry.

Configuration knobs (gateway)

- `Gateway:RateLimits:WindowSeconds` — fixed window length in seconds (default 10). Also controls the `Retry-After` header value.
- `Gateway:RateLimits:Defaults:PermitLimit` and `Gateway:RateLimits:Defaults:QueueLimit` — fallback limits (default 100/100).
- `Gateway:RateLimits:Routes:{prefix}:PermitLimit` and `...:QueueLimit` — per route-family overrides. Built-in defaults:
  - `db` PermitLimit=200, QueueLimit=PermitLimit
  - `storage` PermitLimit=150, QueueLimit=PermitLimit
  - `identity` PermitLimit=600, QueueLimit=0 (no queue to avoid auth timeouts)
  - `dashboard` PermitLimit=300, QueueLimit=PermitLimit
- `Gateway:OutputCache:DefaultTtlSeconds` — base anonymous cache TTL (default 15s)
- `Gateway:OutputCache:StaticTtlSeconds` — static asset policy TTL (default 300s)

Request body size limits (defaults)

- Storage service: 100 MB
- Database service: 10 MB
- Identity service: 2 MB
- Dashboard service: 10 MB

These defaults are enforced at the gateway and may be tuned per environment. Keep them aligned with backend expectations to avoid 413 Payload Too Large errors.

## 11. Multi-Tenancy

- Tenant resolution via host/path
- Header propagation (X-Tansu-Tenant)
- Tenant-aware caching and routing

Resolution rules

- Subdomain and path are supported:
  - Subdomain form: `<tenant>.yourdomain` (e.g., `acme.apps.example.com`).
  - Path form: `/t/{tenant}` (e.g., `https://apps.example.com/t/acme/...`).
- Precedence: when both are present, the path form takes precedence over subdomain.
- After resolution, the gateway sets the `X-Tansu-Tenant` header for downstream services. Clients do not need to send this header explicitly unless using presigned anonymous flows; in those cases, include `X-Tansu-Tenant` as documented in Storage API.

## 12. Troubleshooting

- Common startup issues
- Certificate and TLS errors
- OIDC discovery and token validation

### 12.1 Find slow requests in 60 seconds (SigNoz)

Use this quick path to identify slow endpoints and drill into a trace:

1. Open the SigNoz UI and go to Traces → Explorer.
2. Set Service = the target service (gateway/database/storage/identity/dashboard).
3. Add Filter: duration > 500ms (adjust as needed) and Time range = Last 15 minutes.
4. Sort by duration desc and click the top trace to open the waterfall.
5. In the span list, look for:

- DB spans with high duration (Npgsql/EFCore)
- Redis spans or HybridCache misses
- Gateway proxy span retries or 5xx status

6. Click “Logs” to view correlated log entries (TraceId/SpanId are propagated). Check EventId ranges for quick categorization.

Tip: If no traces appear, run the checks in 12.2 below and ensure traffic is actually hitting the gateway. Health reads often don’t emit DB spans; use a real API call.

### 12.2 No traces arriving

Symptoms: Empty Traces/Logs in SigNoz; apps running.

Checklist

- Collector healthy: signoz-otel-collector container up and listening on 4317/4318.
- Service config: OpenTelemetry:Otlp:Endpoint points to the collector inside the network (dev compose: <http://signoz-otel-collector:4317>).
- Readiness: check /health/ready for each service; exporter queue and W3C format are validated in readiness.
- ClickHouse: time-series tables exist (see Section 8.2 readiness checks). Errors in signoz-query/signoz-otel-collector logs can indicate schema drift.
- Host firewall/VPN: ensure ports 4317/4318 are not blocked between services and collector.

Remediation

- Restart the collector and resend traffic; exporters retry with backoff.
- In dev, toggle log level to Debug temporarily for category OpenTelemetry.* to inspect exporter messages.
- Verify that the Environment is Development so RequireHttpsMetadata is not blocking HTTP in dev, and production uses HTTPS endpoints.

### 12.3 ClickHouse retention exhausted or storage pressure

Symptoms: Recent traces/metrics disappear; disk usage high.

Checklist

- Check ClickHouse disk usage and partitions; retention windows per data type may be too low/high for your traffic.
- Confirm compaction/log flushes: run a targeted LOGS flush.
- Validate that governance defaults were applied (retention/TTL) if you rely on them.

Remediation

- Apply updated retention via governance task (see Section 8.2 → Apply SigNoz governance defaults) and allow background merges.
- Increase disk allocation or reduce sampling/capture volume for verbose categories.
- For dev only: drop and recreate metrics DB if corrupt (see Section 8.2 troubleshooting steps).

### 12.4 Collector backpressure and retry storms

Symptoms: High exporter queue, batch timeouts, delayed signal appearance.

Checklist

- SigNoz collector CPU/memory usage; look for throttling.
- Exporter diagnostics meter (tansu.otel.exporter) for queue size/backoff events in Dashboard metrics page or SigNoz.
- Batch processor settings (maxQueueSize, scheduledDelay, exportTimeout) across services.

Remediation

- Scale collector resources or reduce incoming load temporarily.
- Increase service batch/exporter limits slightly and ensure backoff is bounded; prefer small scheduledDelay for interactive traces.
- Avoid capturing high-cardinality attributes or large logs; ensure body capture is disabled.

## 13. Testing & E2E validation

This platform ships with lightweight health checks and end-to-end (E2E) tests that verify core paths through the gateway, identity, and backend services.

### 13.1 Local quick checks

- Health endpoints (all services):
  - Live: `GET /health/live`
  - Ready: `GET /health/ready`
  - Via gateway for each service: `/`, `/identity/`, `/dashboard/`, `/db/`, `/storage/` then append `/health/live` or `/health/ready`.
- Bring services up (developer workflow):
  - VS Code → Run Task… → "dev: up" to build and start services.
  - VS Code → Run Task… → "dev: down" to stop them.
- Dev database (PostgreSQL) recommended for DB features:
  - Use the persistent Citus/Postgres container described earlier, or the helper script at `dev/tools/start-dev-db.ps1`.

### 13.2 Run E2E tests locally

- The E2E test suite lives under `tests/TansuCloud.E2E.Tests` and now resolves its base URLs directly from `.env` via the shared `TestUrls` helper. The default dev values are:
  - `PUBLIC_BASE_URL=http://127.0.0.1:8080` (browser-visible gateway binding)
  - `GATEWAY_BASE_URL=http://gateway:8080` (in-cluster backchannel; automatically rewritten to `127.0.0.1` when tests run on the host)
  - Override by exporting either variable before invoking `dotnet test`; the helper honours existing environment variables and only falls back to `.env` when unset.
- What is covered today:
  - Health endpoints across services.
  - Identity UI login alias via gateway.
  - Token-based, idempotent tenant provisioning to Database via gateway.
- How to run:
  - Ensure services are up (see 13.1) and that a local PostgreSQL is reachable at `localhost:5432` (the default dev setup).
  - In VS Code, Run Task… → "Run all E2E tests"; or run tests from your IDE/test explorer.
  - Tip: you can run focused suites via provided VS Code tasks (e.g., "Run health E2E tests").
- Inspect the effective base URLs from PowerShell with the shared script helper:

  ```powershell
  pwsh -NoProfile -Command "& { . ./dev/tools/common.ps1; $urls = Resolve-TansuBaseUrls -PreferLoopbackForGateway; $urls }"
  ```

  The same helper is used by VS Code tasks and CI workflows to keep host-facing URLs aligned with `.env`.
- Notes:
  - Tests accept development HTTPS certificates; no extra trust steps needed for local runs.
  - The provisioning test obtains an access token using the dashboard client (scopes: `db.write admin.full`) and calls the Database provisioning API twice, asserting idempotency.
  - Playwright browsers: to avoid long installs during test runs, preinstall browsers once:
    - Option A (recommended): run `pwsh -NoProfile -File tests/TansuCloud.E2E.Tests/playwright.ps1` once; the script is idempotent and skips if already installed.
    - Option B: set `PLAYWRIGHT_SKIP_INSTALL=1` to skip installs in environments where browsers are pre-baked (e.g., CI agents).

### 13.2.1 Garnet/Redis-dependent outbox test gating

Some outbox E2E validation requires a real Garnet or Redis instance (publishing and subscription). These tests are decorated with a custom attribute `RedisFactAttribute` and will be reported as Skipped unless the environment variable `REDIS_URL` is set.

To enable the Garnet/Redis E2E outbox test locally:

1. Start (or ensure) a Garnet/Redis container/service is running and reachable.
2. Export `REDIS_URL` before running tests:

```powershell
$env:REDIS_URL = "localhost:6379" # or full configuration string supported by StackExchange.Redis

dotnet test .\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj -c Debug --filter Full_Dispatcher_Redis_Publish
```

Example skip output when not configured:

```text
Full dispatcher loop publishes to Redis and marks dispatched (E2E) [SKIP]
  REDIS_URL not set; Redis-dependent test skipped
```

Rationale:

- Keeps CI fast and deterministic when Redis isn’t provisioned.
- Avoids false negatives due to missing ephemeral infrastructure.
- Local developers can opt-in for the extra signal when modifying outbox/Redis code paths.

If you routinely run with Redis, consider adding a VS Code test task that sets `REDIS_URL` inline so the attribute no longer skips the test.

### 8.2 Observability dashboards (SigNoz)

The Dashboard still hosts an admin-only entry point at `/dashboard/admin/metrics`, but it now serves as a hand-off to SigNoz instead of rendering native charts. Operators should open the SigNoz UI (Compose default: `http://127.0.0.1:3301/`) for metrics, traces, and logs across all services.

Key points

- All services export OTLP to the local SigNoz collector (`signoz-otel-collector:4317`). No Prometheus instance is required for dev or prod parity.
- The Metrics page in the Dashboard provides a short checklist and direct link to SigNoz. There is no longer an in-app Prometheus proxy or metrics API surface (`/dashboard/api/metrics/*` has been removed).
- SigNoz organizes dashboards under **Metrics → Dashboards**. Start with the "TansuCloud Overview" space to inspect service RPS, error rates, latency, database outbox throughput, and gateway fan-out.
- Traces and logs stream to the same SigNoz instance. Use the built-in Explorer for request drill-downs and click-through from distributed traces to spanning logs.

Quick dev smoke

1. Bring up Compose infra and apps (VS Code tasks: `compose: up infra (pg + redis + pgcat)` then `compose: up apps`).
2. Generate traffic via the gateway (e.g., ping `/storage/health/ready` and `/db/health/ready`).
3. Visit `http://127.0.0.1:3301/` and open the "TansuCloud Overview" dashboard. You should see new time-series points within ~1 minute.

Troubleshooting

- If dashboards are empty, ensure the `signoz-otel-collector` container is healthy and the OTLP endpoint matches `http://signoz-otel-collector:4317` in each service’s configuration.
- For long-running sessions, periodically compact ClickHouse storage (`docker compose exec clickhouse /usr/bin/clickhouse-client --query "SYSTEM FLUSH LOGS"`).
- Authentication: the dev environment seeds SigNoz with the credentials in this guide; in production integrate with your SSO before exposing the UI.

### 13.6 Storage API v1 quick reference

Base path via gateway: `/storage/api`. All requests must include the tenant header `X-Tansu-Tenant: <tenant-id>`. Auth scopes: `storage.read` for reads, `storage.write` for writes. In Development, `admin.full` implies both.

Buckets

- `GET /buckets` — list buckets for the tenant.
- `PUT /buckets/{bucket}` — create a bucket (idempotent).
- `DELETE /buckets/{bucket}` — delete an empty bucket; returns 409 if not empty.

Objects

- `PUT /objects/{bucket}/{key}` — upload an object. Returns 201 Created with a weak `ETag` header.
- `GET /objects/{bucket}/{key}` — download an object. Supports range requests (`Range: bytes=...`) returning 206 and `Content-Range`.
- `HEAD /objects/{bucket}/{key}` — metadata only; returns `ETag`, `Content-Type`, and `Content-Length`.
- `DELETE /objects/{bucket}/{key}` — delete an object.
- `GET /objects?bucket={bucket}&prefix={prefix?}` — list objects in a bucket with optional prefix; items include `Key`, `ETag`, `Length`, `ContentType`, `LastModified`.

Conditional requests

- Lists and items return weak `ETag`s. Send `If-None-Match` on GET to receive `304 Not Modified` when unchanged.
- Send `If-Match` on modifying requests when you need optimistic concurrency; `412 Precondition Failed` on mismatch.

Presigned URLs

- `POST /presign` — generate a temporary signed URL for anonymous access.
  - Body (example):
    - `{"Method":"PUT","Bucket":"bkt","Key":"path/file.txt","ExpirySeconds":300,"MaxBytes":2000000,"ContentType":"text/plain"}`
  - Response: `{ "url": "/storage/api/objects/bkt/path/file.txt?exp=...&sig=...&ct=...&max=...", "expires": 1700000000 }`
  - Enforcement at upload time:
    - `exp` strictly enforced: expired presigned URLs return 403 Forbidden (no clock skew allowance).
    - `max` limits the request `Content-Length`.
    - `ct` enforces Content-Type by media type only (parameters like `charset` are ignored). For example, presigned as `text/plain` accepts uploads with `text/plain; charset=utf-8`.
  - Anonymous client must still include tenant header: `X-Tansu-Tenant`.

Transforms (optional)

- Presign transform URL:
  - `POST /presign/transform` — generate a temporary signed URL for an image transform.
  - Body (example):
    - `{ "Bucket":"bkt","Key":"img/cat.png","Width":800,"Height":0,"Format":"webp","Quality":80,"ExpirySeconds":300 }`
  - Response: `{ "url": "/storage/api/transform/bkt/img/cat.png?w=800&h=0&fmt=webp&q=80&exp=...&sig=...", "expires": 1700000000 }`
- Perform transform:
  - `GET /transform/{bucket}/{key}?w=&h=&fmt=&q=&exp=&sig=`
  - Allowed formats: `webp`, `jpeg`, `png`.
  - Limits and timeouts are enforced (max width/height/total pixels; execution timeout). Requests exceeding limits return 400; expired or invalid signatures return 403.
  - Caching: per-tenant transform cache is keyed by `tenant|bucket|key|sourceETag|fmt|w|h|q`; cache is invalidated automatically when the source object’s ETag changes.
  - ETag: responses reuse the source object’s ETag; `Vary: Accept-Encoding` is set. Content-Type reflects the encoded format.

Response compression

- When clients send `Accept-Encoding: br`, compressible GET responses are served with `Content-Encoding: br` (Brotli). The cache varies by encoding via `Vary: Accept-Encoding`.
- Already-compressed media (e.g., jpg, png, zip, mp4) are not recompressed.

Multipart uploads

- `POST /multipart/{bucket}/initiate/{key}` — returns `{ uploadId }`.
- `PUT /multipart/{bucket}/parts/{partNumber}/{key}?uploadId=...` — upload a part; presign is supported.
- `POST /multipart/{bucket}/complete/{key}?uploadId=...` + body `{ "Parts": [1,2,...] }` — assemble parts into the final object.
- `DELETE /multipart/{bucket}/abort/{key}?uploadId=...` — abort and cleanup.
- Minimum part size is enforced at Complete: all parts except the last must be at least the configured minimum.
- Optional per-part size cap: when `Storage__MultipartMaxPartSizeBytes` > 0, each uploaded part (including the last) must be ≤ that size. Oversized parts are rejected with `413 Payload Too Large` during `PUT /multipart/.../parts/...` and will cause `Complete` to fail validation if somehow bypassed.

Configuration keys (storage service)

- `Storage__RootPath`: Filesystem path for object data (default `/data` inside the container).
- `Storage__PresignSecret`: HMAC secret used to sign presigned URLs.
- `Storage__MultipartMaxPartSizeBytes`: Per-part size cap in bytes for multipart uploads. Use `0` to disable the cap. In development compose, it's set to `1048576` (1 MiB) to exercise E2E coverage.
- Compression options (Brotli): configure under `Storage:Compression` (env prefix `Storage__Compression__*`). Common keys include enabling for HTTP/HTTPS, Brotli level, and MIME allowlist.
- Transform options: configure under `Storage:Transforms` (env prefix `Storage__Transforms__*`), including allowed formats, max width/height/total pixels, default quality, timeout, and cache TTL.

Usage and quotas

- `GET /usage` — returns tenant usage: `{ totalBytes, objectCount, maxTotalBytes?, maxObjectCount?, maxObjectSizeBytes? }`.
- Quotas apply to regular and presigned uploads; violations return RFC7807 ProblemDetails with a descriptive reason.

Admin Dashboard

- The Dashboard includes an admin page at `/admin/storage` that surfaces:
  - Tenant usage header
  - Bucket list/create/delete
  - Object list by prefix (Key, ETag, Length, Content-Type, Last-Modified) and per-object delete
  - Presigned anonymous upload flow with content-type and max-bytes controls
  - HEAD metadata viewer for ETag/Content-Type/Length

Notes

- Always include `X-Tansu-Tenant` on requests through the gateway, including anonymous presigned calls.
- ETags are weak and stable per object content; they enable efficient conditional requests and caching through the gateway.
- Keys in routes may be URL-encoded by clients; the Storage service normalizes keys by unescaping percent-encoding before presign validation. Presign canonicalization uses the unescaped key to avoid signature mismatches for keys with encoded slashes or spaces.

### 13.7 Tenant provisioning API (quick reference)

Base path via gateway: `/db/api`. This API provisions a tenant database, applies migrations, and seeds initial roles/config.

- `POST /provisioning/tenants` — idempotent provisioning call.
  - Body example:
    - `{ "tenantId": "acme", "displayName": "Acme, Inc." }`
  - Auth (production): requires a privileged token. Recommended scopes: `admin.full` or a dedicated provisioning role with `db.write` authority per your policies.
  - Dev bypass (local/E2E only): you may set header `X-Provision-Key: letmein` to bypass auth for quick testing.
  - Idempotency: subsequent calls with the same `tenantId` succeed without duplicating work.
  - Response: HTTP 200/201 with a minimal status payload; failures return RFC7807 ProblemDetails with diagnostics.

### 13.5 Database API v1 quick reference

Base path via gateway: `/db/api` (tenant required via `X-Tansu-Tenant` header). Auth scopes: `db.read` for reads, `db.write` for writes (in Development, `admin.full` implies both).

- Collections
  - `GET /collections` — list with pagination; supports weak ETag on the collection set.
  - `GET /collections/{id}` — get by id; weak ETag; `If-None-Match` → 304.
  - `POST /collections` — create.
  - `PUT /collections/{id}` — update; `If-Match` required for conditional update; 412 on mismatch.
  - `DELETE /collections/{id}` — delete; `If-Match` supported; 412 on mismatch.

- Documents
  - `GET /documents` — list with filters/sort and pagination.
    - Query: `collectionId` (Guid?), `createdAfter`|`createdBefore` (RFC3339), `sortBy` in `id|collectionId|createdAt`, `sortDir` in `asc|desc`, `page` (>=1), `pageSize` (1..500).
    - Weak ETag on the result; `If-None-Match` → 304.
  - `GET /documents/{id}` — get by id; weak ETag; `If-None-Match` → 304.
  - `POST /documents` — create; body includes `collectionId`, optional `embedding` (float[1536]) and `content` (JSON object). JSON payloads are stored as `jsonb`.
  - `PUT /documents/{id}` — update; supports `If-Match`; 412 on mismatch.
  - `DELETE /documents/{id}` — delete; supports `If-Match`; 412 on mismatch.

- Vector search (pgvector)
  - `POST /documents/search/vector` — KNN within a collection. Body: `collectionId` (Guid), `embedding` (float[1536]), `limit` (default 10).
  - `POST /documents/search/vector-global` — ANN across collections with a two-step per-collection cap.
  - Indexing: HNSW indexes are created by migrations when `vector` extension is available; otherwise sequential scan is used.

ETags and conditional requests

- Lists and items return weak ETags. If the ETag you send in `If-None-Match` matches, you’ll get `304 Not Modified`.
- `PUT`/`DELETE` accept `If-Match`; if it doesn’t match the current ETag, you’ll get `412 Precondition Failed`.

Development diagnostics

- During development and E2E, the Database service adds `X-Tansu-Db` to responses to surface the normalized tenant database name (e.g., `tansu_tenant_e2e_server_ank`). This header is not intended for production.

### 13.3 CI pipeline (GitHub Actions)

- CI workflow file: `.github/workflows/e2e.yml`.
- What it does:
  - Provisions a PostgreSQL service (localhost:5432) for the Database service.
  - Builds the solution, launches services in the background, waits for the gateway to become ready, then runs the E2E test project.
  - Publishes TRX test results as an artifact.
- When it runs:
  - On pushes and pull requests targeting `master`.

### 13.4 Troubleshooting test failures

- Gateway not ready:
  - Confirm `http://localhost:8080/health/ready` (or your configured HTTPS URL) returns 200. If not, inspect gateway logs and dependent service health endpoints (see 13.1).
- Database provisioning failures:
  - Ensure PostgreSQL is reachable at `localhost:5432` and credentials match dev settings.
  - Verify the `Provisioning` options in `TansuCloud.Database` (AdminConnectionString, extensions) align with your environment.
- Certificate issues:
  - Local tests ignore TLS validation by design. For browser access, trust the dev cert or enable TLS on the gateway as described in section 5.
- Identity/token issues:
  - Check `/.well-known/openid-configuration` via gateway under `/identity` and verify `/identity/connect/token` is reachable.

## 14. ML Management (Preview)

This section introduces optional, non-breaking conventions and a preview admin page to prepare for Task 34 (ML recommendations and predictions). It does not change runtime behavior yet.

Where to access

- Dashboard page: /dashboard/admin/ml (preferred) or /admin/ml if your gateway exposes a root alias.
- Requires admin access to the Dashboard.

Storage model artifact conventions

- Suggested object layout: models/{modelName}/{version}/...
- Tag artifacts with these metadata keys when uploading:
  - x-tansu-model-name — model identifier (e.g., recommender-v1)
  - x-tansu-model-version — semantic version or build/build-id (e.g., 1.0.0)
  - x-tansu-framework — model runtime/format (ml.net, onnx, pytorch)
  - x-tansu-checksum — sha256 of content
  - x-tansu-created-at — ISO 8601 timestamp
  - x-tansu-source — pipeline/job id or URL

Gateway metrics placeholders (for future wiring)

- Metrics defined but not emitted by default:
  - ml_recommendations_served (counter, items)
  - ml_inference_latency_ms (histogram, ms)
  - ml_recommendation_coverage_pct (observable gauge, percent)

What you can do now

- Organize and upload model artifacts using the conventions above so they’re ready for future rollout.
- Preview the admin UI under ML Management to see planned fields (default model/version, framework, coverage target). Saving is disabled until Task 34.

Next steps (when Task 34 starts)

- Enable saving tenant-level defaults and switch-over controls in the Dashboard.
- Expose admin APIs to list/select available models based on tagged artifacts in Storage.
- Wire Gateway metrics and add dashboards/alerts.

## Appendix A: Docker Compose Examples

- Base compose
- Enabling TLS with volumes and env
- Overriding ports and networks

### A.1 Enable TLS on the gateway (dev or prod)

```yaml
services:
  gateway:
    image: tansucloud-gateway
    ports:
      - "80:8080"    # HTTP
      - "443:8443"   # HTTPS (container listens on 8443)
    volumes:
      - ./certs:/certs:ro
    environment:
      - Kestrel__Endpoints__Https__Url=https://0.0.0.0:8443
      - Kestrel__Endpoints__Https__Certificate__Path=/certs/gateway.pfx
      - GATEWAY_CERT_PASSWORD=${GATEWAY_CERT_PASSWORD}
    depends_on:
      identity:
        condition: service_healthy
      dashboard:
        condition: service_healthy
      db:
        condition: service_healthy
      storage:
        condition: service_healthy
```

Place your `gateway.pfx` under `./certs` and set `GATEWAY_CERT_PASSWORD` in your shell or a `.env` file.

## Appendix B: Configuration Keys

- Services:*BaseUrl
- Oidc:Issuer, Oidc:Authority (dashboard)
- OpenTelemetry:Otlp:Endpoint
- Gateway:Cors:AllowedOrigins
- Kestrel:Endpoints:Http/Https (or env: Kestrel__Endpoints__Https__Url, Kestrel__Endpoints__Https__Certificate__Path)

## Appendix C: Useful Endpoints

- / (gateway health text)
- /health/live, /health/ready (all services)
- /identity/.well-known/openid-configuration
- /dashboard (UI)

### Notes

- This guide is a living document. Fill in environment-specific details (DNS, certs, backends) as they are finalized.

## Appendix D: .env example (production-like)

Place this `.env` beside `docker-compose.yml` and adjust values to your environment.

```env
PUBLIC_BASE_URL=https://app.yourdomain.com

DASHBOARD_CLIENT_SECRET=your-strong-secret

POSTGRES_USER=postgres
POSTGRES_PASSWORD=your-db-password
PGCAT_ADMIN_USER=admin
PGCAT_ADMIN_PASSWORD=your-pgcat-password

# If enabling TLS on the gateway with a PFX mounted under ./certs
GATEWAY_CERT_PASSWORD=pfx-password
```

## Garnet and Outbox (Database service)

Purpose

- The Outbox pattern ensures reliable event emission: write domain changes and an "outbox event" in the same database transaction. A background worker publishes the event to Garnet later.
- Garnet is a Redis-compatible cache server from Microsoft with better performance and lower memory footprint.

Local/dev Garnet

- Docker Compose includes `ghcr.io/microsoft/garnet:latest` with a named volume `tansu-redisdata` and default port 6379.
- The Database service depends on Garnet when Outbox is enabled.
- Garnet is fully compatible with existing Redis clients (StackExchange.Redis) without code changes.

Outbox configuration (Database service)

- Outbox is disabled unless a Garnet/Redis connection is provided.
- Keys (env or appsettings):
  - `Outbox:RedisConnection` (e.g., `redis:6379` in compose - the service name remains "redis" for backwards compatibility)
  - `Outbox:Channel` (default `tansu.outbox`)
  - `Outbox:PollSeconds` (default `2`)
  - `Outbox:BatchSize` (default `100`)
  - `Outbox:MaxAttempts` (default `8`)

Idempotency for write requests

- Clients may send an `Idempotency-Key` header on write operations (e.g., create document). The Database service records the key and will return the original outcome on safe retries.
- Use a strong, unique value per logical operation. Expire/rotate keys as needed on the client side.
  - The key is stored in `outbox_events.idempotency_key` with a partial unique index to prevent duplicates (when not null). The server may retain keys for operational analysis; clients should not reuse keys across unrelated requests.

Operations

- Health: Garnet container exposes `PING`-compatible health checks; verify container is healthy before DB starts processing Outbox.
- Observability: Outbox worker logs dispatched, retried, and dead-lettered events. Metrics are exported via OpenTelemetry when configured.
- Failure handling: After max attempts, events transition to a dead-letter state; operators should inspect and decide to replay or ignore.
  - Channel: events are published to the configured channel (default `tansu.outbox`) as JSON envelopes including `{ tenant, collectionId?, documentId?, op }`. Consumers should treat the payload as a contract subject to additive changes.
