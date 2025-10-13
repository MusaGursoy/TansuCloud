# Telemetry Service SQLite Permissions Fix

## Problem

The telemetry service was failing to start with the error:
```
SQLite Error 8: 'attempt to write a readonly database'
```

This occurred because:
1. The service uses a chiseled .NET runtime image that runs as a non-root user (`app:app`) by default
2. The SQLite database file is stored in a Docker volume mounted at `/var/opt/tansu/telemetry/`
3. When the volume was first mounted, it didn't have proper permissions for the non-root user to write to it

## Solution

Modified `TansuCloud.Telemetry/Dockerfile` to pre-create the telemetry directory with proper permissions using the busybox layer:

### Changes Made

```dockerfile
# Final runtime image (chiseled, non-root by default)
FROM busybox:stable-musl AS bb
RUN mkdir -p /var/opt/tansu/telemetry && \
    chmod -R 777 /var/opt/tansu/telemetry

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS final
# ... other directives ...
COPY --from=bb --chown=app:app /var/opt/tansu/telemetry /var/opt/tansu/telemetry
VOLUME ["/var/opt/tansu/telemetry"]
```

### Why This Works

1. **Busybox Layer**: We use the busybox layer (which has shell commands) to create the directory structure with world-writable permissions (777)
2. **COPY with chown**: We copy the directory structure from the busybox layer to the final image, using `--chown=app:app` to set ownership to the non-root user
3. **Volume Mount**: When Docker mounts the volume, it preserves the directory structure and permissions from the image layer

### Key Considerations

- **Chiseled Images**: Chiseled .NET runtime images don't have shell (`/bin/sh`), so we can't use `RUN` commands directly in the final stage
- **Non-Root by Default**: Chiseled images run as the `app` user (UID 64198) for security
- **Permission Mode**: We use `777` (world-writable) in busybox to ensure the volume mount works regardless of the host system's UID mapping
- **Security**: For production, consider using a more restrictive permission model or external persistent storage

## Verification

After the fix:

1. **Service starts successfully**:
   ```bash
   docker compose up -d telemetry
   # Service becomes healthy after ~15 seconds
   ```

2. **Health check passes**:
   ```bash
   curl http://127.0.0.1:5279/health/ready
   # Returns: {"status":"Healthy",...}
   ```

3. **E2E tests pass**:
   ```bash
   dotnet test --filter "FullyQualifiedName~TelemetryServiceE2E"
   # Total tests: 2, Passed: 2
   ```

## Related Files

- `TansuCloud.Telemetry/Dockerfile` - Fixed Dockerfile with proper permissions
- `docker-compose.yml` - Volume configuration for telemetry service
- `tests/TansuCloud.E2E.Tests/TelemetryServiceE2E.cs` - E2E tests validating the fix

## Alternative Solutions Considered

1. **Using a full base image**: Would work but increases image size significantly
2. **Init container pattern**: More complex, requires additional orchestration
3. **External volume initialization**: Would require manual setup before first run
4. **Host-mounted directory**: Works but less portable across environments

The chosen solution balances security, simplicity, and portability while maintaining the benefits of chiseled images.
