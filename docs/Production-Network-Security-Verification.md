# Production Network Security Verification

**Date**: 2025-10-11  
**Status**: ✅ **VERIFIED**

## Security Principle: Gateway-Only Exposure

In production, **only the Gateway service** exposes ports to the host network. All other services communicate internally within the Docker network (`tansucloud-network`).

## Port Exposure Audit

### ✅ Services with Host-Exposed Ports (CORRECT)

| Service | Exposed Ports | Purpose | Status |
|---------|---------------|---------|--------|
| **gateway** | 80:8080 | HTTP traffic (production) | ✅ Correct |
| **gateway** | 443:8443 (commented) | HTTPS traffic (when enabled) | ✅ Correct |

### ✅ Services with NO Host-Exposed Ports (CORRECT - Internal Only)

All the following services use **internal-only networking** and are **NOT accessible from the host**:

#### Application Services

| Service | Internal Port | Docker Network Only | OTLP Export |
|---------|---------------|---------------------|-------------|
| **identity** | 8080 | ✅ Internal | ✅ <http://signoz-otel-collector:4317> |
| **dashboard** | 8080 | ✅ Internal | ✅ <http://signoz-otel-collector:4317> |
| **db** | 8080 | ✅ Internal | ✅ <http://signoz-otel-collector:4317> |
| **storage** | 8080 | ✅ Internal | ✅ <http://signoz-otel-collector:4317> |

#### Infrastructure Services

| Service | Internal Port | Docker Network Only | Metrics Export |
|---------|---------------|---------------------|----------------|
| **postgres** | 5432 | ✅ Internal | Via postgres-exporter |
| **redis** | 6379 | ✅ Internal | Via redis-exporter |
| **pgcat** | 6432 (postgres), 9930 (admin) | ✅ Internal | N/A |

#### Prometheus Exporters

| Service | Internal Port | Docker Network Only | Scrape Target |
|---------|---------------|---------------------|---------------|
| **postgres-exporter** | 9187 | ✅ Internal (expose only) | Scraped by collector |
| **redis-exporter** | 9121 | ✅ Internal (expose only) | Scraped by collector |

#### Observability Stack (Profile: observability)

| Service | Internal Port | Docker Network Only | Purpose |
|---------|---------------|---------------------|---------|
| **signoz-otel-collector** | 4317 (gRPC), 4318 (HTTP) | ✅ Internal (expose only) | OTLP receiver |
| **signoz-query-service** | 8080 | ✅ Internal (expose only) | Query API |
| **signoz-frontend** | 3301 | ✅ Internal (expose only) | UI (dev: mapped to host) |
| **clickhouse** | 9000 (native), 8123 (HTTP) | ✅ Internal (expose only) | Storage |
| **zookeeper** | 2181 | ✅ Internal (expose only) | Coordination |

## OTLP Export Configuration

### Application Services → SigNoz Collector (Internal Network)

All application services export telemetry to the **internal OTLP collector** within the Docker network:

```yaml
environment:
  OpenTelemetry__Otlp__Endpoint: ${OTLP_ENDPOINT:-http://signoz-otel-collector:4317}
```

**Key Points**:

- ✅ Uses **internal Docker hostname** `signoz-otel-collector`
- ✅ Port 4317 is **NOT exposed to host** (uses `expose:` not `ports:`)
- ✅ Falls back to appsettings.json default if env var not set
- ✅ No external network traffic for telemetry

### Prometheus Exporters → SigNoz Collector (Internal Scraping)

The collector scrapes infrastructure metrics via **internal Docker network**:

```yaml
# In signoz-otel-collector-config.yaml
receivers:
  prometheus:
    config:
      scrape_configs:
        - job_name: 'postgres'
          static_configs:
            - targets: ['postgres-exporter:9187']  # Internal hostname
        
        - job_name: 'redis'
          static_configs:
            - targets: ['redis-exporter:9121']  # Internal hostname
```

**Key Points**:

- ✅ Scrapes via **internal Docker hostnames**
- ✅ Exporter ports use `expose:` not `ports:` (internal only)
- ✅ No host network involvement

## Network Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                         Host Network                          │
│                                                               │
│  ┌──────────────────────────────────────────────┐           │
│  │  Port 80:8080 (HTTP)                         │           │
│  │  Port 443:8443 (HTTPS - when enabled)       │           │
│  └─────────────────┬────────────────────────────┘           │
│                    │                                          │
└────────────────────┼──────────────────────────────────────────┘
                     │
                     │ ✅ ONLY GATEWAY EXPOSED
                     │
┌────────────────────┼──────────────────────────────────────────┐
│                    ▼                                          │
│             ┌──────────────┐            tansucloud-network    │
│             │   Gateway    │                                  │
│             │  (port 8080) │                                  │
│             └──────┬───────┘                                  │
│                    │                                          │
│         ┌──────────┼──────────────────────────┐              │
│         │          │          │        │       │              │
│    ┌────▼───┐ ┌───▼────┐ ┌──▼──┐ ┌───▼────┐ │              │
│    │Identity│ │Dashboard│ │ DB  │ │Storage │ │              │
│    │  :8080 │ │  :8080  │ │:8080│ │ :8080  │ │              │
│    └────┬───┘ └───┬─────┘ └──┬──┘ └───┬────┘ │              │
│         │         │           │        │      │              │
│         └─────────┴───────────┴────────┴──────┘              │
│                          │                                    │
│                          │ OTLP Export                        │
│                          │                                    │
│                   ┌──────▼─────────────┐                     │
│                   │ signoz-otel-       │                     │
│                   │ collector:4317     │                     │
│                   │ (NOT exposed)      │                     │
│                   └──────┬─────────────┘                     │
│                          │                                    │
│              ┌───────────┼───────────┐                       │
│              │           │           │                        │
│     ┌────────▼──┐   ┌───▼────┐  ┌──▼────────┐              │
│     │ClickHouse │   │Query   │  │Frontend   │              │
│     │   :9000   │   │Service │  │  :3301    │              │
│     │ (internal)│   │ :8080  │  │(dev: host)│              │
│     └───────────┘   └────────┘  └───────────┘              │
│                                                               │
│     ┌──────────┐   ┌────────────┐   ┌──────────────┐       │
│     │Postgres  │   │   Redis    │   │    PgCat     │       │
│     │  :5432   │   │   :6379    │   │ :6432,:9930  │       │
│     │(internal)│   │ (internal) │   │  (internal)  │       │
│     └────┬─────┘   └─────┬──────┘   └──────────────┘       │
│          │               │                                    │
│     ┌────▼──────┐   ┌───▼────────┐                          │
│     │postgres-  │   │redis-      │                          │
│     │exporter   │   │exporter    │                          │
│     │  :9187    │   │  :9121     │                          │
│     │(internal) │   │ (internal) │                          │
│     └───────────┘   └────────────┘                          │
│              │            │                                   │
│              └────────────┘                                  │
│                     │                                         │
│                     │ Prometheus Scrape                      │
│                     └───────────► (back to collector)        │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

## Security Verification Checklist

- ✅ **Only Gateway** exposes ports to host network (80 and optionally 443)
- ✅ **All application services** communicate internally via Docker network
- ✅ **OTLP collector** is internal-only (4317/4318 NOT exposed to host)
- ✅ **Database services** (PostgreSQL, Redis, PgCat) are internal-only
- ✅ **Prometheus exporters** use `expose:` not `ports:` (internal-only)
- ✅ **SigNoz components** (ClickHouse, Query Service, Frontend) are internal
- ✅ **Telemetry export** uses internal Docker hostnames (`signoz-otel-collector:4317`)
- ✅ **No localhost/127.0.0.1** references in production OTLP endpoints
- ✅ **Metrics scraping** happens via internal Docker network
- ✅ **Both compose files validate** successfully

## Production Deployment Notes

### Default Configuration (Minimal)

```bash
docker compose -f docker-compose.prod.yml up -d
```

- ✅ Core services only (Gateway, Identity, Dashboard, DB, Storage)
- ✅ Infrastructure (PostgreSQL, Redis, PgCat)
- ✅ **NO observability stack** (no SigNoz, no exporters)
- ✅ Gateway exposed on port 80

### With Observability Profile

```bash
docker compose -f docker-compose.prod.yml --profile observability up -d
```

- ✅ All core services + Infrastructure
- ✅ **SigNoz stack** (collector, query service, frontend, ClickHouse, Zookeeper)
- ✅ **Prometheus exporters** (postgres-exporter, redis-exporter)
- ✅ All telemetry **internal-only** (no host exposure except Gateway)

### SigNoz UI Access (Development vs Production)

**Development** (`docker-compose.yml`):

```yaml
signoz-frontend:
  ports:
    - "3301:3301"  # ✅ Mapped to host for easy access
```

**Production** (`docker-compose.prod.yml`):

```yaml
signoz-frontend:
  expose:
    - "3301"  # ❌ NOT mapped to host (internal-only)
```

To access SigNoz UI in production, you have two options:

1. **Reverse proxy through Gateway** (recommended):
   - Add a route in Gateway for `/observability/*` → `signoz-frontend:3301`
   - Protect with authentication (admin-only)

2. **SSH tunnel or VPN** (secure):

   ```bash
   ssh -L 3301:localhost:3301 production-host
   ```

   Then access <http://localhost:3301> on your local machine

## Conclusion

✅ **Production network security is correctly configured**:

- Gateway is the only externally accessible service
- All telemetry (OTLP, Prometheus) flows internally
- No unnecessary port exposures
- Infrastructure services are isolated
- Observability stack is internal-only

The architecture follows **zero-trust principles** with **minimal attack surface**.

---

**Verified By**: Automated compose validation + Manual review  
**Last Updated**: 2025-10-11  
**Related Documents**:

- `Task08-Infrastructure-Telemetry-Completion.md`
- `Telemetry-Service-Production-Resilience.md`
- `Guide-For-Admins-and-Tenants.md` § 8.3
