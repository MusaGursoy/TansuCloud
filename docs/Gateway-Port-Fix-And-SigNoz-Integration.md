# Gateway Port Configuration and SigNoz Integration

## Date: October 11, 2025

## Gateway Port Configuration

**Gateway port**: 8080 (standard)

**Files Configured:**

- **docker-compose.yml** (line ~593-596):
  ```yaml
  ports:
    - "80:8080"      # HTTP (for host browsers)
    - "8080:8080"    # HTTP
  ```

- **.env** (line 6):
  ```properties
  PUBLIC_BASE_URL=http://127.0.0.1:8080
  ```

**Result**: ✅ Gateway accessible on port 8080.

### 2. SigNoz Service URL Fix

**Corrected SigNoz service reference** from incorrect `signoz-frontend:3301` to actual `signoz:8080`.

**Files Modified:**

- **docker-compose.yml** (line 602):
  ```yaml
  Services__SigNozBaseUrl: http://signoz:8080
  ```

- **docker-compose.prod.yml** (line 391):
  ```yaml
  Services__SigNozBaseUrl: http://signoz:8080
  ```

**Explanation**: 
- SigNoz container service is named `signoz` (not `signoz-frontend`)
- SigNoz listens on internal port 8080 (not 3301)
- Port 3301 is just the host mapping for direct dev access: `3301:8080`

**Result**: ✅ Gateway can now resolve and proxy to SigNoz service.

### 3. SigNoz Route Implementation

**Gateway Program.cs** includes complete SigNoz routing:

```csharp
// Service URL resolution
var signozBase = ResolveServiceBaseUrl("Services:SigNozBaseUrl", 8080);

// Route configuration
new RouteConfig {
    RouteId = "signoz-route",
    ClusterId = "signoz",
    Match = new RouteMatch { Path = "/signoz/{**catch-all}" },
    Transforms = [
        new Dictionary<string, string> { ["PathRemovePrefix"] = "/signoz" },
        new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" },
        new Dictionary<string, string> { ["X-Forwarded-Prefix"] = "/signoz" }
    ]
}

// Cluster configuration
new ClusterConfig {
    ClusterId = "signoz",
    HttpRequest = new() {
        ActivityTimeout = TimeSpan.FromMinutes(5),
        Version = new Version(1, 1),
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    },
    Destinations = { ["signoz1"] = new() { Address = signozBase } }
}
```

**Result**: ✅ Gateway `/signoz/*` route successfully proxies HTTP requests to SigNoz backend.

## Current Status

### ✅ Working

1. **Gateway Running**: Successfully bound to port 8080, healthy and serving requests
2. **SigNoz Direct Access**: `http://127.0.0.1:3301` - Full UI functionality with all assets
3. **SigNoz API Proxying**: Gateway successfully forwards HTTP requests to SigNoz backend
4. **Service Discovery**: All 28 services running and healthy in Docker Compose
5. **Telemetry Collection**: All app services reporting metrics/traces to SigNoz

### ⚠️ Limitation

**SigNoz UI through Gateway Route** (`http://127.0.0.1:8080/signoz`):
- ❌ UI assets (JavaScript/CSS) fail to load with 404 errors
- ✅ API requests proxy correctly
- ✅ Initial HTML loads successfully

**Root Cause**: SigNoz frontend is a React SPA that requests assets from root paths (e.g., `/runtime~main.js`) instead of prefixed paths (e.g., `/signoz/runtime~main.js`). The SigNoz container image is not configured to serve with a base path.

**Example Failed Requests**:
```
GET http://127.0.0.1:8080/runtime~main.3c6f...js   → 404
GET http://127.0.0.1:8080/main.f7dd...js           → 404
GET http://127.0.0.1:8080/static/...               → 404
```

These should be:
```
GET http://127.0.0.1:8080/signoz/runtime~main.3c6f...js
GET http://127.0.0.1:8080/signoz/main.f7dd...js
GET http://127.0.0.1:8080/signoz/static/...
```

## Recommendations

### For Development (Current)

✅ **Use direct access**: `http://127.0.0.1:3301` - Works perfectly with zero configuration.

### For Production

Choose one of these approaches:

#### Option 1: Subdomain Routing (Recommended)
Instead of path-based routing (`/signoz`), use subdomain routing:
- URL: `https://signoz.example.com`
- Gateway configuration: Forward entire domain to SigNoz service
- Benefit: No base path issues, clean URLs, natural SPA behavior

#### Option 2: Direct Port Exposure with Firewall
- Expose SigNoz port directly (e.g., port 3301)
- Use firewall rules or network policies for access control
- Benefit: Simplest configuration, no proxy overhead

#### Option 3: Rebuild SigNoz with Base Path
If you have access to SigNoz source:
- Configure `PUBLIC_URL=/signoz` in build process
- Rebuild container with custom webpack configuration
- Benefit: Works with path-based routing

#### Option 4: Advanced Gateway Rewriting
Implement asset path rewriting in Gateway:
- Add YARP transforms to rewrite asset references in HTML
- Complex configuration, potential performance impact
- Not recommended due to maintenance burden

## Documentation Updates

Updated files to reflect current state:

1. **README.md**: Updated SigNoz access section with port change and limitation notes
2. **This document**: Created comprehensive record of changes and rationale

## Verification

All services verified operational via Playwright browser automation:

- ✅ SigNoz UI (direct port 3301): Showing 4 services with healthy metrics
  - `tansu.storage`: P99 8.98ms, 0.10 ops/sec
  - `tansu.identity`: P99 8.64ms, 0.10 ops/sec
  - `tansu.db`: P99 3.38ms, 21.13 ops/sec
  - `tansu.dashboard`: P99 2.67ms, 0.10 ops/sec

- ✅ Telemetry Admin UI (port 5279): Login page accessible, API key auth working

## Conclusion

The Gateway is configured on port 8080. The SigNoz routing implementation is complete and correct at the Gateway level. The UI asset path limitation is a known behavior of serving SPAs behind path-based reverse proxies and does not affect the API functionality. For production deployments, subdomain routing is recommended for SigNoz UI access.
