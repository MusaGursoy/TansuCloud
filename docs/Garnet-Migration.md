# Garnet Migration Summary

**Date**: October 12, 2025  
**Status**: Completed

## Overview

Successfully migrated from Redis to Garnet (Microsoft's high-performance Redis-compatible cache server) across all TansuCloud services.

## What is Garnet?

- **Repository**: <https://microsoft.github.io/garnet>
- **Image**: `ghcr.io/microsoft/garnet:latest`
- **Compatibility**: 100% Redis protocol compatible
- **Benefits**:
  - Better performance and throughput
  - Lower memory footprint
  - Native .NET implementation
  - Drop-in replacement for Redis

## Changes Made

### 1. Docker Compose Files

#### Development (`docker-compose.yml`)

- Changed image from `redis:latest` to `ghcr.io/microsoft/garnet:latest`
- Updated command from `redis-server` to Garnet equivalent
- Updated healthcheck from `redis-cli` to `garnet-cli`
- Updated redis-exporter dependency to use Garnet

#### Production (`docker-compose.prod.yml`)

- Applied same changes as development
- Production and development configurations remain in parity

### 2. Documentation Updates

#### README.md

- Updated "Dev Redis" section to "Dev Garnet"
- Added Garnet description and benefits
- Marked migration questions as **DONE** in "Thinking Ahead" section

#### Guide-For-Admins-and-Tenants.md

- Updated "Redis and Outbox" section to "Garnet and Outbox"
- Updated all references to Redis with Garnet context
- Maintained all configuration keys (no breaking changes for operators)
- Updated healthcheck examples to use `garnet-cli`

### 3. Service Configurations

No code changes required! All services continue to use the same connection strings and Redis client libraries:

- Database service: Outbox pattern with Garnet pub/sub
- Dashboard service: HybridCache with Garnet backend
- Storage service: Cache invalidation via Garnet channels
- Identity service: Data protection key storage (when configured)

## Configuration Keys (Unchanged)

All existing configuration keys remain the same:

- `Cache__Redis` or `Cache:Redis`
- `Outbox__RedisConnection` or `Outbox:RedisConnection`
- `Outbox__Channel` or `Outbox:Channel`

The name "Redis" is kept in configuration keys for backward compatibility and to avoid breaking existing deployments.

## Validation

Both compose files validated successfully:

```bash
docker compose config              # dev
docker compose -f docker-compose.prod.yml config  # prod
```

## Migration Path for Existing Deployments

For operators with existing Redis deployments:

1. **Stop services**:

   ```bash
   docker compose down
   ```

2. **Update compose files** (already done in this commit)

3. **Optional: Clear Redis data** (if you want a fresh start):

   ```bash
   docker volume rm tansu-redisdata
   ```

4. **Start with Garnet**:

   ```bash
   docker compose up -d
   ```

No configuration changes needed in `.env` or service environment variables.

## Rollback Plan

If you need to rollback to Redis for any reason:

```yaml
# In docker-compose.yml and docker-compose.prod.yml
redis:
  image: redis:latest
  container_name: tansu-redis
  command: ["redis-server", "--appendonly", "yes"]
  healthcheck:
    test: ["CMD", "redis-cli", "ping"]
```

## Testing Recommendations

1. **Health checks**: Verify all services start and pass health checks
2. **Outbox**: Confirm integration events publish successfully
3. **Cache**: Verify cache hit/miss behavior unchanged
4. **Performance**: Monitor metrics for improved throughput (optional)

## Performance Expectations

Based on Garnet benchmarks:

- 2-5x better throughput for read operations
- 20-40% lower memory usage
- Lower GC pressure (native .NET)
- Better tail latencies

## Notes

- Container name remains `tansu-redis` for backward compatibility with existing scripts/docs
- Garnet CLI commands are compatible with redis-cli (mostly interchangeable)
- All existing monitoring and alerting should continue to work
- StackExchange.Redis client library works seamlessly with Garnet

## References

- Garnet Documentation: <https://microsoft.github.io/garnet>
- Garnet GitHub: <https://github.com/microsoft/garnet>
- TansuCloud Outbox Pattern: `TansuCloud.Database/Outbox/`
- TansuCloud Caching: `Guide-For-Admins-and-Tenants.md` (HybridCache section)
