# SigNoz Gateway Route Implementation (DEPRECATED)

> **⚠️ DEPRECATED**: This document describes a feature that was removed on October 12, 2025.
>
> The SigNoz gateway reverse proxy route has been removed. See [SigNoz-Gateway-Proxy-Removal.md](./SigNoz-Gateway-Proxy-Removal.md) for details.
>
> **Current access pattern**:
> - Development: Direct access at `http://127.0.0.1:3301`
> - Production: See [SigNoz-API-Integration-Guide.md](./SigNoz-API-Integration-Guide.md) for API-based integration

---

**Original Date**: October 11, 2025  
**Removed**: October 12, 2025  
**Context**: Added reverse proxy route in Gateway to access SigNoz UI through the single gateway entry point

## Overview

~~SigNoz UI is now accessible through the Gateway at `/signoz/*`, providing a unified access point for all services including observability dashboards.~~

**This feature was removed because**:
1. SigNoz React SPA assets failed to load under path-based proxying
2. Production deployments don't need to expose full SigNoz UI through the gateway
3. Future integration will use SigNoz REST API to embed data in Admin Dashboard

## Implementation Details

### Gateway Configuration (`TansuCloud.Gateway/Program.cs`)

**Service Base URL** (line ~588):
```csharp
var signozBase = ResolveServiceBaseUrl("Services:SigNozBaseUrl", 3301);
```

**Route Configuration** (after storage route):
```csharp
new RouteConfig
{
    RouteId = "signoz-route",
    ClusterId = "signoz",
    Match = new RouteMatch { Path = "/signoz/{**catch-all}" },
    Transforms = new[]
    {
        new Dictionary<string, string> { ["PathRemovePrefix"] = "/signoz" },
        new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
        new Dictionary<string, string>
        {
            ["RequestHeader"] = "X-Forwarded-Prefix",
            ["Set"] = "/signoz"
        },
        new Dictionary<string, string> { ["RequestHeadersCopy"] = "true" },
        new Dictionary<string, string> { ["ResponseHeadersCopy"] = "true" }
    }
}
```

**Cluster Configuration**:
```csharp
new ClusterConfig
{
    ClusterId = "signoz",
    HttpRequest = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(5),
        Version = new Version(1, 1),
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    },
    Destinations = new Dictionary<string, DestinationConfig>
    {
        ["signoz1"] = new() { Address = signozBase }
    }
}
```

**HttpClient Hardening**: Added `signoz` to cluster list in `ConfigureHttpClient` to apply standard security settings.

### Docker Compose Configuration

**Development (`docker-compose.yml`)**:
```yaml
Services__SigNozBaseUrl: http://signoz-frontend:3301
```

**Production (`docker-compose.prod.yml`)**:
```yaml
Services__SigNozBaseUrl: http://signoz-frontend:3301
```

## Access Points

### Via Gateway (Recommended)
- **URL**: `http://127.0.0.1:8080/signoz`
- **Production**: `https://your-domain.com/signoz`
- **Benefits**:
  - Single entry point
  - Consistent with other services
  - Works in all environments (dev/prod)
  - No additional port exposure needed

### Direct Access (Dev Only)
- **URL**: `http://127.0.0.1:3301`
- **Note**: Only available in development compose; production does not expose this port directly
- **Use case**: Quick access during local debugging

## Routing Behavior

1. **Path stripping**: Gateway removes `/signoz` prefix before forwarding to SigNoz frontend
2. **Headers preserved**: Original host and forwarding headers maintained
3. **Prefix indication**: `X-Forwarded-Prefix: /signoz` set for downstream awareness
4. **HTTP/1.1**: Forced to avoid h2c negotiation issues
5. **Timeout**: 5-minute activity timeout (same as other services)

## Configuration Override

To use a different SigNoz instance (e.g., external SigNoz deployment):

**Environment variable**:
```bash
Services__SigNozBaseUrl=http://external-signoz:3301
```

**appsettings override** (not recommended; prefer env vars):
```json
{
  "Services": {
    "SigNozBaseUrl": "http://external-signoz:3301"
  }
}
```

## Security Considerations

- **No authentication**: Gateway does not enforce auth for `/signoz/*` route; SigNoz has its own authentication
- **Internal network**: SigNoz frontend communicates only within Docker internal network
- **Production**: Ensure SigNoz credentials are secured and consider additional reverse proxy auth if needed

## Testing

### Manual Test (Dev)
1. Start services: `docker compose up -d`
2. Wait for services to be healthy
3. Navigate to: `http://127.0.0.1:8080/signoz`
4. Should see SigNoz login page

### Automated Test
No specific E2E test added yet. Consider adding to `TansuCloud.E2E.Tests`:
```csharp
[Fact]
public async Task Gateway_SigNoz_Route_Returns_UI()
{
    var response = await _client.GetAsync("/signoz");
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var content = await response.Content.ReadAsStringAsync();
    content.Should().Contain("SigNoz"); // or other expected content
}
```

## Related Documentation

- **README.md**: Updated with SigNoz access instructions
- **Guide-For-Admins-and-Tenants.md**: Contains detailed SigNoz configuration and usage
- **Architecture.md**: Should be updated to reflect gateway routing topology

## Future Enhancements

1. **Authentication**: Add gateway-level auth for `/signoz/*` route
2. **Rate limiting**: Apply appropriate rate limits for dashboard access
3. **Output caching**: Consider caching static SigNoz assets
4. **HTTPS**: Enable HTTPS endpoint when TLS is configured
5. **Monitoring**: Add gateway-specific metrics for SigNoz route usage
