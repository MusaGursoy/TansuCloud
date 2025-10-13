# Garnet Migration Test Checklist

Use this checklist to validate the Garnet migration before deploying to production.

## Pre-Migration

- [ ] Review `docs/Garnet-Migration.md` for overview
- [ ] Backup `.env` file
- [ ] Note current Redis data volume size: `docker volume inspect tansu-redisdata`
- [ ] Export critical Redis data if needed (optional)

## Development Environment

### 1. Compose Validation

- [x] Validate dev compose: `docker compose config`
- [x] Validate prod compose: `docker compose -f docker-compose.prod.yml config`

### 2. Start Services

- [ ] Stop existing services: Run task "compose: down"
- [ ] Start with Garnet: Run task "compose: up"
- [ ] Wait for all services to be healthy (check `docker compose ps`)

### 3. Basic Health Checks

- [ ] Gateway health: `curl http://127.0.0.1:8080/health/ready`
- [ ] Identity health: via Gateway `/identity/health/ready`
- [ ] Database health: via Gateway `/db/health/ready`
- [ ] Storage health: via Gateway `/storage/health/ready`
- [ ] Dashboard health: via Gateway `/dashboard/health/ready`

### 4. Garnet-Specific Checks

- [ ] Container running: `docker ps | grep tansu-redis`
- [ ] Garnet logs: `docker logs tansu-redis` (should see "Garnet" in startup)
- [ ] Garnet CLI: `docker exec tansu-redis garnet-cli PING` (should return PONG)
- [ ] Check keys: `docker exec tansu-redis garnet-cli KEYS *`

### 5. Outbox Pattern (Database Service)

- [ ] Provision a test tenant: Run task "Provision tenant via Gateway (dev bypass)"
- [ ] Create a document via Database API (triggers outbox event)
- [ ] Check outbox table: Verify events are created and dispatched
- [ ] Monitor Garnet pub/sub: `docker exec tansu-redis garnet-cli MONITOR`
- [ ] Verify events published to `tansu.outbox` channel

### 6. Cache Behavior

- [ ] Make a cacheable request (e.g., GET document)
- [ ] Check Garnet keys: `docker exec tansu-redis garnet-cli KEYS *cache*`
- [ ] Verify cache hit on subsequent request (check response headers or logs)
- [ ] Test cache invalidation (update a document)
- [ ] Verify cache entry removed/updated

### 7. Dashboard SignalR (if using Redis backplane)

- [ ] Cache eviction logs appear in Database/Storage services
- [ ] Provision a test tenant and verify cache invalidation
- [ ] Login to Dashboard at <http://127.0.0.1:8080/dashboard>
- [ ] Verify Dashboard loads and shows metrics
- [ ] Navigate between pages (verify real-time updates work)
- [ ] Check for WebSocket connection errors in browser console

### 8. E2E Tests

- [ ] Run health tests: Run task "Run health E2E tests"
- [ ] Run full E2E suite: Run task "Run all E2E tests" (optional)

## Production Environment (Staging First)

### 1. Pre-Deploy

- [ ] Review production compose file: `docker-compose.prod.yml`
- [ ] Backup production Garnet/Redis data
- [ ] Schedule maintenance window
- [ ] Notify stakeholders

### 2. Deploy

- [ ] Stop production services
- [ ] Pull latest images: `docker compose -f docker-compose.prod.yml pull`
- [ ] Start with Garnet: `docker compose -f docker-compose.prod.yml up -d`

### 3. Smoke Tests

- [ ] All containers healthy: `docker compose -f docker-compose.prod.yml ps`
- [ ] Gateway accessible
- [ ] Test tenant login
- [ ] Create/read/update operations work
- [ ] Check application logs for errors

### 4. Performance Monitoring (48 hours)

- [ ] Monitor Garnet memory usage (should be lower)
- [ ] Monitor request latency (should be similar or better)
- [ ] Monitor cache hit rate (should be similar)
- [ ] Check for any timeout errors
- [ ] Review SigNoz metrics/traces

### 5. Rollback Plan (if needed)

- [ ] Document current state
- [ ] Stop services
- [ ] Change compose to use `redis:latest`
- [ ] Restore Redis data from backup
- [ ] Restart services
- [ ] File incident report

## Post-Migration

- [ ] Update runbooks with Garnet-specific commands
- [ ] Train team on Garnet CLI differences (if any)
- [ ] Update monitoring dashboards (labels, queries)
- [ ] Document any performance improvements observed
- [ ] Archive Redis data backups (per retention policy)

## Troubleshooting

### Garnet won't start

```bash
# Check logs
docker logs tansu-redis

# Try manual start
docker run --rm -p 6379:6379 ghcr.io/microsoft/garnet:latest
```

### Services can't connect to Garnet

```bash
# Verify network
docker network inspect tansucloud-network

# Test connection from another container
docker compose exec db sh -c "nc -zv redis 6379"
```

### Outbox events not publishing

```bash
# Check Database service logs
docker logs tansu-db | grep -i outbox

# Monitor Garnet pub/sub
docker exec tansu-redis garnet-cli PSUBSCRIBE 'tansu.*'

# Check Outbox table
# (Use PostgreSQL MCP or psql to query outbox_events table)
```

### Cache misses higher than expected

```bash
# Check Garnet memory
docker stats tansu-redis

# Check eviction policy
docker exec tansu-redis garnet-cli CONFIG GET maxmemory-policy

# Review cache keys
docker exec tansu-redis garnet-cli KEYS *
```

## Success Criteria

✅ All health checks passing  
✅ Outbox events publishing successfully  
✅ Cache hit rate similar to Redis baseline  
✅ No increase in error rates  
✅ Memory usage lower than Redis  
✅ E2E tests passing  
✅ No user-reported issues for 48 hours  

## Notes

- Garnet CLI is mostly compatible with redis-cli
- Container name `tansu-redis` unchanged for compatibility
- All environment variables unchanged (Cache__Redis, Outbox__RedisConnection)
- Garnet uses same port 6379 internally
- StackExchange.Redis client works seamlessly

## Support

- Garnet Issues: <https://github.com/microsoft/garnet/issues>
- TansuCloud Docs: `docs/Garnet-Migration.md`
- Team: [Your team contact info]
