# SigNoz Gateway Proxy Removal - Summary

## Date
October 12, 2025

## Overview
Removed the SigNoz reverse proxy configuration from the TansuCloud Gateway to simplify the architecture and align with production deployment patterns.

## Changes Made

### 1. Gateway Configuration (TansuCloud.Gateway/Program.cs)

**Removed**:
- `signozBase` URL resolution (line 589)
- SigNoz route configuration (`signoz-route`)
- SigNoz cluster configuration (`signoz` cluster)
- SigNoz cluster check in `ConfigureHttpClient`

**Impact**: The gateway no longer proxies requests to `/signoz/*`. SigNoz UI is accessed directly in development.

### 2. Documentation Updates

#### README.md
- Removed recommendation to access SigNoz through gateway at `/signoz`
- Updated "SigNoz UI Access" section to clarify development vs production access patterns
- Added guidance for production: embed data via API, restrict access, or use subdomain

#### Guide-For-Admins-and-Tenants.md
- Clarified that SigNoz is accessed directly at `http://127.0.0.1:3301` in development
- Added production guidance: not exposed through gateway, access via API integration or secure network policies
- Maintained existing quick start and readiness check instructions

### 3. New Documentation

**docs/SigNoz-API-Integration-Guide.md** - Comprehensive guide for future work:
- SigNoz REST API endpoints reference (traces, metrics, logs, service topology)
- Authentication approach using API keys
- Implementation recommendations (backend proxy service, UI components)
- Sample query patterns for common observability scenarios
- Security considerations and production deployment options
- Next steps for integration into Admin Dashboard Observability pages

## Access Patterns

### Development
- **SigNoz UI**: Direct access at `http://127.0.0.1:3301`
- **OTLP Export**: Services continue to export to `http://signoz-otel-collector:4317`
- **No Change**: All telemetry collection continues to work as before

### Production
- **SigNoz UI**: Not exposed to end users or operators through the gateway
- **Options for Operators**:
  1. (Recommended) Embed observability data in Admin Dashboard using SigNoz API
  2. Restrict direct access via secure network policies (VPN, IP allowlist)
  3. Expose on separate subdomain with authentication (e.g., `observability.example.com`)
- **OTLP Export**: Services continue to export to internal collector (no host port)

## Rationale

1. **UI Asset Issues**: SigNoz React SPA is not designed for path-based proxying; assets fail to load under `/signoz` prefix
2. **Production Security**: Exposing full SigNoz UI through gateway is unnecessary; observability dashboards are for ops/admin only
3. **API-First Approach**: Future Admin Dashboard will embed relevant observability data using SigNoz REST API, providing curated views
4. **Simplification**: Reduces gateway configuration complexity; removes unused/broken route

## Build Verification

- ✅ Solution builds successfully: `dotnet build TansuCloud.sln`
- ✅ Docker compose config validates: `docker-compose.yml`
- ✅ Docker compose prod config validates: `docker-compose.prod.yml`
- ✅ No compilation errors

## Testing Impact

No impact on existing E2E tests:
- Health endpoint tests continue to work
- SigNoz readiness checks remain unchanged (direct port access)
- OTLP export and telemetry collection unchanged

## Future Work

See `docs/SigNoz-API-Integration-Guide.md` for implementation roadmap:
1. Create backend service proxy for SigNoz API
2. Design Admin Dashboard Observability page layouts
3. Build Blazor components for traces, metrics, logs visualization
4. Implement caching and performance optimization
5. Add admin authorization and audit logging

## References

- [SigNoz Documentation](https://signoz.io/docs/)
- [SigNoz Query API](https://signoz.io/docs/userguide/query-builder/)
- Project guideline: "App service URLs and OIDC behind Gateway" (copilot-instructions.md)

---

*This change aligns with the project principle of maintaining parity between development and production environments while simplifying unnecessary complexity.*
