# Infrastructure Telemetry Quick Reference

## Metrics Endpoints (Development)

| Service | Exporter | Port | Endpoint | Container Name |
|---------|----------|------|----------|----------------|
| PostgreSQL | postgres-exporter | 9187 | http://127.0.0.1:9187/metrics | tansu-postgres-exporter |
| PgCat | pgcat-exporter | 9188 | http://127.0.0.1:9188/metrics | tansu-pgcat-exporter |
| Redis | redis-exporter | 9121 | http://127.0.0.1:9121/metrics | tansu-redis-exporter |

## Key Metrics by Service

### PostgreSQL (postgres-exporter)

```prometheus
# Connection Pool
pg_stat_database_numbackends              # Active connections
pg_settings_max_connections               # Maximum connections allowed

# Query Performance
pg_stat_database_tup_fetched             # Rows fetched
pg_stat_database_tup_returned            # Rows returned
pg_stat_database_blks_hit                # Cache hits
pg_stat_database_blks_read               # Disk reads

# Replication
pg_stat_replication_lag_bytes            # Replication lag in bytes
pg_replication_lag_seconds               # Replication lag in seconds

# Locks and Deadlocks
pg_locks_count                           # Current locks
pg_stat_database_deadlocks               # Deadlock count

# Transaction Stats
pg_stat_database_xact_commit             # Committed transactions
pg_stat_database_xact_rollback           # Rolled back transactions
```

**Cache Hit Ratio Calculation**:
```
hit_ratio = pg_stat_database_blks_hit / (pg_stat_database_blks_hit + pg_stat_database_blks_read)
```
Target: > 90%

### PgCat (pgcat-exporter)

```prometheus
# Connection Pool Stats
pgcat_active_connections                 # Active connections per pool
pgcat_idle_connections                   # Idle connections per pool
pgcat_queued_clients                     # Clients waiting for connection
pgcat_pool_saturation                    # Pool utilization percentage

# Backend Health
pgcat_backend_health_status              # Backend health (1=healthy, 0=unhealthy)
pgcat_backend_connections                # Connections per backend

# Query Routing
pgcat_queries_routed_total               # Total queries routed
pgcat_queries_per_pool                   # Queries by pool

# Errors
pgcat_connection_errors_total            # Connection errors
pgcat_query_errors_total                 # Query routing errors
```

### Redis (redis-exporter)

```prometheus
# Memory
redis_memory_used_bytes                  # Total memory used
redis_memory_max_bytes                   # Maximum memory configured
redis_mem_fragmentation_ratio            # Memory fragmentation

# Keys and Operations
redis_db_keys                            # Number of keys per database
redis_db_keys_expiring                   # Keys with TTL
redis_commands_total                     # Commands executed by type

# Performance
redis_commands_duration_seconds_total    # Command latency
redis_instantaneous_ops_per_sec          # Operations per second

# Evictions and Expirations
redis_evicted_keys_total                 # Evicted keys (memory pressure)
redis_expired_keys_total                 # Expired keys

# Hit Rate
redis_keyspace_hits_total                # Cache hits
redis_keyspace_misses_total              # Cache misses

# Connections
redis_connected_clients                  # Current connections
redis_rejected_connections_total         # Rejected due to maxclients
redis_blocked_clients                    # Clients blocked on operations
```

**Cache Hit Ratio Calculation**:
```
hit_ratio = redis_keyspace_hits_total / (redis_keyspace_hits_total + redis_keyspace_misses_total)
```
Target: > 80%

## Quick Health Checks

### PowerShell (Windows)

```powershell
# Check all exporters are running
docker ps --filter "name=exporter" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

# Test metrics endpoints
$endpoints = @(
    "http://127.0.0.1:9187/metrics",
    "http://127.0.0.1:9188/metrics",
    "http://127.0.0.1:9121/metrics"
)
foreach ($url in $endpoints) {
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
    Write-Host "✓ $url - Status: $($response.StatusCode)"
}

# Check OTLP collector is scraping
docker logs signoz-otel-collector --tail 100 | Select-String "postgres|pgcat|redis"
```

### Bash/Zsh (macOS/Linux)

```bash
# Check all exporters are running
docker ps --filter "name=exporter" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

# Test metrics endpoints
for port in 9187 9188 9121; do
    curl -s -o /dev/null -w "✓ Port $port: %{http_code}\n" http://127.0.0.1:$port/metrics
done

# Check OTLP collector is scraping
docker logs signoz-otel-collector --tail 100 | grep -E "postgres|pgcat|redis"
```

## Common PromQL Queries (SigNoz)

### PostgreSQL

```promql
# Connection pool utilization
(pg_stat_database_numbackends / pg_settings_max_connections) * 100

# Cache hit ratio
rate(pg_stat_database_blks_hit[5m]) / 
  (rate(pg_stat_database_blks_hit[5m]) + rate(pg_stat_database_blks_read[5m]))

# Active long-running queries
pg_stat_activity_max_tx_duration{state="active"} > 30

# Replication lag
pg_replication_lag_seconds

# Deadlocks per minute
rate(pg_stat_database_deadlocks[1m])
```

### PgCat

```promql
# Pool saturation
pgcat_pool_saturation > 90

# Queued clients
pgcat_queued_clients

# Unhealthy backends
pgcat_backend_health_status{status="unhealthy"}

# Connection errors rate
rate(pgcat_connection_errors_total[5m])
```

### Redis

```promql
# Memory utilization
(redis_memory_used_bytes / redis_memory_max_bytes) * 100

# Cache hit ratio
rate(redis_keyspace_hits_total[5m]) / 
  (rate(redis_keyspace_hits_total[5m]) + rate(redis_keyspace_misses_total[5m]))

# Eviction rate
rate(redis_evicted_keys_total[1m])

# Command latency p95
histogram_quantile(0.95, rate(redis_commands_duration_seconds_bucket[5m]))

# Connected clients
redis_connected_clients
```

## Alert Rule Examples (SigNoz)

### PostgreSQL Alerts

```yaml
- alert: PostgreSQLHighConnectionUsage
  expr: (pg_stat_database_numbackends / pg_settings_max_connections) * 100 > 80
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "PostgreSQL connection pool usage above 80%"

- alert: PostgreSQLLowCacheHitRatio
  expr: |
    rate(pg_stat_database_blks_hit[5m]) / 
    (rate(pg_stat_database_blks_hit[5m]) + rate(pg_stat_database_blks_read[5m])) < 0.9
  for: 10m
  labels:
    severity: warning
  annotations:
    summary: "PostgreSQL cache hit ratio below 90%"

- alert: PostgreSQLReplicationLag
  expr: pg_replication_lag_seconds > 10
  for: 3m
  labels:
    severity: critical
  annotations:
    summary: "PostgreSQL replication lag above 10 seconds"
```

### PgCat Alerts

```yaml
- alert: PgCatHighPoolSaturation
  expr: pgcat_pool_saturation > 90
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "PgCat pool saturation above 90%"

- alert: PgCatQueuedClients
  expr: pgcat_queued_clients > 10
  for: 2m
  labels:
    severity: warning
  annotations:
    summary: "PgCat has clients waiting for connections"

- alert: PgCatUnhealthyBackend
  expr: pgcat_backend_health_status{status="unhealthy"} > 0
  for: 1m
  labels:
    severity: critical
  annotations:
    summary: "PgCat detected unhealthy backend"
```

### Redis Alerts

```yaml
- alert: RedisHighMemoryUsage
  expr: (redis_memory_used_bytes / redis_memory_max_bytes) * 100 > 80
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "Redis memory usage above 80%"

- alert: RedisHighEvictionRate
  expr: rate(redis_evicted_keys_total[1m]) > 100
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "Redis evicting more than 100 keys per minute"

- alert: RedisLowHitRatio
  expr: |
    rate(redis_keyspace_hits_total[5m]) / 
    (rate(redis_keyspace_hits_total[5m]) + rate(redis_keyspace_misses_total[5m])) < 0.8
  for: 10m
  labels:
    severity: warning
  annotations:
    summary: "Redis cache hit ratio below 80%"

- alert: RedisHighLatency
  expr: histogram_quantile(0.95, rate(redis_commands_duration_seconds_bucket[5m])) > 0.01
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "Redis command latency p95 above 10ms"
```

## Troubleshooting

### Exporter Not Starting

```powershell
# Check logs
docker logs tansu-postgres-exporter
docker logs tansu-pgcat-exporter
docker logs tansu-redis-exporter

# Common issues:
# - Connection refused: Check target service is running
# - Authentication failed: Verify credentials in DATA_SOURCE_NAME
# - Port conflict: Check if port is already in use
```

### Metrics Not Appearing in SigNoz

```powershell
# 1. Verify exporter is exposing metrics
curl http://127.0.0.1:9187/metrics | Select-String "pg_"

# 2. Check collector scrape configuration
docker exec signoz-otel-collector cat /etc/otel-collector-config.yaml | Select-String "postgres|pgcat|redis"

# 3. Check collector logs for scrape errors
docker logs signoz-otel-collector | Select-String "error|failed" | Select-String "postgres|pgcat|redis"

# 4. Verify collector can reach exporters
docker exec signoz-otel-collector wget -O- http://postgres-exporter:9187/metrics
```

### Stale Metrics

```powershell
# Check exporter health
docker ps --filter "name=exporter" --format "{{.Names}}: {{.Status}}"

# Restart exporter if needed
docker restart tansu-postgres-exporter

# Restart collector to clear cache
docker restart signoz-otel-collector
```

## Production Checklist

- [ ] Pin exporter image versions (no `:latest` tags)
- [ ] Configure resource limits on exporters
- [ ] Use read-only database credentials
- [ ] Keep exporter endpoints internal (no public exposure)
- [ ] Set appropriate scrape intervals (30s dev, 60-120s prod)
- [ ] Configure SigNoz retention policies
- [ ] Set up critical alerts (connection pool, memory, replication lag)
- [ ] Test exporter failover scenarios
- [ ] Document alert runbooks
- [ ] Enable authentication on PgCat admin interface

## Additional Resources

- **PostgreSQL Exporter**: https://github.com/prometheus-community/postgres_exporter
- **Redis Exporter**: https://github.com/oliver006/redis_exporter
- **Prometheus Best Practices**: https://prometheus.io/docs/practices/naming/
- **SigNoz Docs**: https://signoz.io/docs/
- **Task 08 Implementation**: `docs/Task08-Infrastructure-Telemetry-Completion.md`
- **Admin Guide**: `Guide-For-Admins-and-Tenants.md` § 8.3
