// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
# TansuCloud – AI Coding Guidelines and Project Context

These guidelines apply to all code generation, refactoring, documentation, and reviews across this repository. Favor modern .NET practices, maintainability, and clear separation of concerns.

## Core Principles
- Prefer modern .NET features and idioms (.NET 9 as of today), including async/await, nullable reference types, dependency injection, logging, configuration, and health checks.
- Whenever we need to use a package in our solution, make sure we use the latest stable version suitable for .NET 9 (avoid outdated/legacy packages unless there is a documented reason).
- Maintain a clean, maintainable codebase with clear separation of concerns (controllers thin; business logic in services; data access in repositories or EF Core DbContexts; configuration via Options pattern).
- Apply SOLID, clean architecture boundaries, and avoid leaking infrastructure concerns into domain logic.
- Write testable code (interfaces, dependency injection, minimal side effects). Add unit/integration tests where changes affect behavior.
- Use cancellation tokens, structured logging, and consistent error handling (ProblemDetails for APIs) when appropriate.

### NuGet Package Management
- When installing NuGet packages, always add them using the latest stable version available for the target project (e.g., prefer `dotnet add package <PackageName>` without pinning a version unless a documented exception applies).

## End-of-block Comments (Long-lived Maintenance Aid)
Adopt explicit end-of-block comments to improve code folding and long-term readability in multi-member types:
- Classes: `} // End of Class ClassName`
- Methods: `} // End of Method MethodName`
- Constructors: `} // End of Constructor ClassName`
- Properties: `} // End of Property PropertyName`

Apply these consistently for non-trivial members and files with multiple classes or long types.

## Documentation and Explanations
- Search Microsoft's latest (.NET 9 as of today) official documentation (Microsoft Learn/Docs) whenever needed, especially for .NET, ASP.NET Core, EF Core, C#, and Azure. Prefer first-party guidance and current practices.
- Add explanatory comments whenever needed to clarify non-obvious intent, invariants, thread-safety, performance-sensitive code paths, and public surface behaviors. Use XML doc comments for public APIs.

## Mandatory File Header
Add the following exact line at the very top of every .cs file you create or edit in this repository:

// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

Keep this as the first line (above using directives, shebangs, etc.) in .cs files.

**Exception:** Do NOT add this header to Blazor component files (.razor files). Blazor components should start with their markup or @-directives without any file header comments.

## Coding Style and Patterns
- Enable nullable reference types and treat warnings seriously; prefer explicit null-handling.
- Use records and readonly structs when immutability is beneficial; prefer init-only setters for DTOs.
- Prefer async methods suffixed with "Async" and pass CancellationToken to I/O and long-running operations.
- Keep controllers thin; validate inputs (DataAnnotations/FluentValidation), delegate to services, return ProblemDetails for errors.
- Configuration via IOptions<T> (Options pattern); no hard-coded settings or secrets in code.
- Favor minimal APIs or conventional controllers based on project consistency; keep endpoint mapping discoverable.
- Log with structured templates (e.g., logger.LogInformation("Processing {OrderId}", orderId)). Avoid logging sensitive data.

## API Documentation (OpenAPI/Scalar)
- **Standard**: Use native .NET 9 OpenAPI generation (`builder.Services.AddOpenApi()` and `app.MapOpenApi()`) with Scalar UI (`app.MapScalarApiReference()`) for all TansuCloud REST APIs.
- **Package**: Add `Scalar.AspNetCore` NuGet package (latest stable) to API projects. Swashbuckle is deprecated and removed as of Task 22.5.
- **Configuration**:
  - Enable XML documentation: `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in .csproj
  - Suppress XML doc warnings if needed: `<NoWarn>$(NoWarn);1591</NoWarn>`
  - Configure OpenAPI document metadata (title, version, description, contact) via `AddDocumentTransformer`
  - Add JWT Bearer security scheme to document (Authorization header, ApiKey type)
  - Apply security requirement to all operations
- **Endpoints**:
  - OpenAPI spec: `/openapi/v1.json` (served by `MapOpenApi()`)
  - Interactive UI: `/scalar/v1` (served by `MapScalarApiReference()`)
  - Both accessible via Gateway with path prefixes (e.g., `/db/scalar/v1`, `/storage/scalar/v1`)
- **Development Only**: API documentation endpoints are gated by `if (app.Environment.IsDevelopment())` - not exposed in production
- **Gateway Integration**: Gateway allows anonymous access to `/*/scalar` and `/*/openapi` paths in Development for ease of use
- **Benefits over Swashbuckle**:
  - Native .NET 9 support (no third-party dependency for spec generation)
  - Modern UI with better visualization of JSON Patch, HTTP Range Requests, and streaming endpoints
  - Active maintenance (Swashbuckle deprecated in .NET 9)
  - Faster load times and better OpenAPI 3.1 compliance

## Testing and Quality
- Add or update unit tests for new behavior and critical bug fixes. Prefer xUnit with fluent assertions if available.
- Prefer small, focused tests over broad, fragile ones. Cover happy path + 1–2 edge cases.
- Keep public behavior backward-compatible unless called out with migration notes.

## Project Specs and Task Acceptance
- Follow these repository documents as sources of truth and align implementations accordingly: `Requirements.md`, `Architecture.md`, and `Tasks.md`.
- Conflict precedence: Requirements > Architecture > Tasks > Code comments. If updates are needed, revise the docs alongside code.
- When marking any task "Completed", also review and update `Guide-For-Admins-and-Tenants` as needed to reflect changes (deployment/config, TLS/certs, endpoints, health checks, ops). Include doc updates in the same PR.
- A task can be marked completed only after its Acceptance criteria are tested and verified. Provide evidence via automated tests and/or documented manual steps (screenshots, logs, or Playwright MCP runs) and reference them in the PR.

Conversation cadence for tasks (every step/iteration/session)
- At the beginning: explicitly state the current Main Task and the active SubTask (e.g., "Task 13: Storage service core → SubTask: Multipart min-size enforcement").
- Then briefly state what you will do next in this step.
- At the end of the step: report what you did/changed, which requirements or acceptance criteria were covered (map them explicitly), and what remains or what you will do next. Also update relevant Tasks-Mx.md file (x for 1-2-3-4).

## Web App Testing with Playwright MCP Tools
- Use Playwright MCP tools for web app development testing.
- Copilot can load the web app, type, login, and click.
- Analyse screenshots and browser console logs to test and validate the web app.
- Also leverage navigation and interaction (navigate, click, type, fill, select, drag, upload), visuals (screenshot, accessibility snapshot), and telemetry (console messages, network requests) as needed.
- Apply pragmatic waits/retries to reduce flakiness; prefer robust selectors and avoid brittle timing assumptions.
- Keep credentials and secrets out of logs; use test accounts and mask sensitive values.

## Database validation with PostgreSQL MCP Tools
- When a task involves programmatically modifying PostgreSQL (DDL/DML/migrations, provisioning, seeding), also use the PostgreSQL MCP tools to validate intent vs. actual state.
  - Connect to the standard dev Postgres (typically localhost:5432 or the mapped host port used by your environment) and inspect schema with the read-only context tool before making changes.
  - After applying changes, query the database (read-only) to confirm that the expected objects/rows exist and idempotency holds. Prefer explicit validation queries tied to the literals you used.
  - For bulk or destructive operations, surface a preview script for review first, then execute in a single, well-formatted statement.
  - Always disconnect when done.
  - Keep credentials out of code; prefer env vars or user secrets. For dev-only convenience, the default local connection string may be used when explicitly documented in the repo.

## Security and Compliance
- Don’t commit secrets. Use configuration providers (user secrets, environment variables, Key Vault) and secure defaults.
- Validate and sanitize external inputs. Apply authorization and authentication consistently.

### New production configuration variables (mandatory)
- If production requires a new URL, user id, password, secret, or key and it does not already exist in `.env`:
  - Add a new, clearly named variable to `.env` (UPPER_SNAKE_CASE). Provide a safe dev-only default or leave empty with an inline comment.
  - Wire the variable through both `docker-compose.yml` and `docker-compose.prod.yml` via the shared env-file. Keep the two compose files consistent per the compose consistency rules below.
  - Consume the value in code via configuration (IConfiguration/IOptions) — do NOT hardcode literals in source. Prefer strongly-typed options where appropriate.
  - Do not check real secrets into the repo. For local dev, use `.env`, user-secrets, or test-only values; for prod, use environment variables or a secret provider (e.g., Key Vault).
  - Update `Guide-For-Admins-and-Tenants.md` with the new variable, its purpose, and how to set it in each environment. If tests depend on it, adjust test fixtures to read from configuration instead of literals.

## Performance and Reliability
- Use CancellationToken, IAsyncEnumerable where beneficial, and avoid synchronous-over-async.
- Be mindful of allocations and logging levels on hot paths; prefer pooling/caching where appropriate and safe.

## JSON Serialization Best Practices

### System.Text.Json Case Sensitivity (Critical)
- **System.Text.Json is case-sensitive by default** (unlike Newtonsoft.Json which defaults to case-insensitive)
- When accepting external JSON (APIs, tests, config files), always use case-insensitive deserialization:
  ```csharp
  var options = new JsonSerializerOptions
  {
      PropertyNameCaseInsensitive = true
  };
  var config = JsonSerializer.Deserialize<ConfigType>(json, options);
  ```
- **Why this matters:** Without this option, JSON property names must exactly match C# property names (case-sensitive). Mismatches silently use default values from C# record/class definitions instead of the JSON values.
- **Common pitfall:** Tests send camelCase JSON (`ttlSeconds: 300`), C# expects PascalCase (`TtlSeconds`). Without case-insensitive deserialization, the default value (e.g., `TtlSeconds = 60`) is used instead of 300, causing tests to fail mysteriously.
- **Debugging tip:** When JSON deserialization seems incorrect, log the deserialized object's actual values (not just the input JSON) to identify case mismatch issues quickly.

### C# Record Default Values
- Be aware that C# record default values can mask deserialization failures
- Example:
  ```csharp
  public record CacheConfig
  {
      public int TtlSeconds { get; init; } = 60;  // Default used if JSON property doesn't match
  }
  ```
- If JSON sends `ttlSeconds` (camelCase) and deserialization is case-sensitive, the default value (60) is used instead of the JSON value (300)
- Always test deserialization with actual JSON payloads to ensure property matching works correctly

## HTTP Client IP Extraction (Proxied Environments)

When extracting client IP addresses for rate limiting, IP-based policies, or logging in proxied/load-balanced environments:

1. **Always check `X-Forwarded-For` header first** (for proxied requests, tests, and production LB scenarios)
2. **Fallback to `Connection.RemoteIpAddress`** only for direct connections
3. **Handle comma-separated lists** in X-Forwarded-For (take first IP for original client)

Example implementation:
```csharp
static string GetClientIp(HttpContext http, Dictionary<string, string> headers)
{
    // Check X-Forwarded-For header first (for tests and proxied requests)
    if (headers.TryGetValue("X-Forwarded-For", out var forwardedFor) 
        && !string.IsNullOrWhiteSpace(forwardedFor))
    {
        var firstIp = forwardedFor.Split(',')[0].Trim();
        return $"ip:{firstIp}";
    }
    
    // Fallback to connection RemoteIpAddress
    var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return $"ip:{remoteIp}";
}
```

**Why this matters:** `Connection.RemoteIpAddress` only works for direct TCP connections. In proxied environments (nginx, Gateway, load balancers, Docker networking), it returns the proxy's IP, not the original client's IP. E2E tests that set `X-Forwarded-For` headers will fail if your code doesn't check this header first.

## Docker Compose Deployment Best Practices

### Code Change Deployment
- **`docker compose restart <service>`** does NOT pick up code changes
  - Only restarts the existing container with the existing image
  - Use for config/env changes only
- **`docker compose up -d --build <service>`** rebuilds and deploys code changes
  - Rebuilds the Docker image from current source code
  - Creates and starts a new container with the updated image
  - Required workflow for any code modifications

### Development Workflow
```bash
# After code changes:
docker compose up -d --build gateway

# After config/env changes only:
docker compose restart gateway
```

**Lesson learned:** During debugging, multiple test failures persisted after code fixes because `restart` was used instead of `--build`. The old buggy code was still running in the container.

### E2E Testing Workflow (MANDATORY)

**Before running E2E tests after ANY code changes:**

1. **ALWAYS rebuild containers** to pick up fresh code:
   ```bash
   docker compose up -d --build
   ```
   - Use the direct `docker compose` command (NOT PowerShell wrappers)
   - Build ALL services to ensure consistency
   - Wait for all services to become healthy

2. **Then run the full E2E test suite**:
   ```bash
   dotnet test .\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj -c Debug
   ```

**Why this matters:** Running tests against stale containers produces false failures. The code you just fixed may not be running in the container. Always rebuild first to ensure you're testing the current codebase, not yesterday's version.

By following these rules, contributors and AI assistants will produce modern, maintainable, and well-documented .NET code for TansuCloud.

## No New Hardcoded URLs (mandatory)

To prevent configuration drift and OIDC/gateway mismatches, do NOT introduce new hardcoded HTTP/HTTPS URLs anywhere in the codebase.

- Derive all externally visible links from configuration:
  - Prefer `PublicBaseUrl` (env: `PUBLIC_BASE_URL`).
  - Fallback to `GatewayBaseUrl` (env: `GATEWAY_BASE_URL`) only when the public base is not available.
  - For OIDC in services, use the existing `Oidc:Issuer` and optional `Oidc:MetadataAddress` keys per the standard below.
- Never inline literals like `http://127.0.0.1:8080`, `http://localhost:8080`, service DNS names, or tenant hostnames directly in application code, views, or client scripts.
- Compose/gateway-internal addresses must also be resolved from configuration or service discovery; do not bake service hostnames into code.
- Tests and documentation:
  - Unit/integration tests should use configuration or test fixtures to get base URLs. If a literal is unavoidable for a test, ensure it is covered by the allowed list used by `LoopbackLiteralGuardTests`.
  - Documentation may contain copy-pasteable examples with loopback literals strictly for local development. Keep such literals within the documented allowlist to avoid CI failures.
- Enforcement: CI includes `LoopbackLiteralGuardTests.Loopback_literals_are_not_reintroduced_outside_allowlist`. If you need to add a new literal for docs/tests, update the allowlist and justify it in the PR.

Rationale: All services and UI must compute browser-visible links from one configured origin to guarantee consistent issuers, cookies, and redirects across dev/compose/production.

## Compose files consistency (dev/prod)

- We maintain exactly two Compose files:
  - `docker-compose.yml` for Development
  - `docker-compose.prod.yml` for Production (with optional profiles like observability)
- When you update `docker-compose.yml`, also update `docker-compose.prod.yml` appropriately as well (and vice versa). Keep service definitions, environment keys, health checks, ports, and dependencies in sync unless there is a documented, intentional deviation for that environment.
- Validate both files after changes:
  - `docker compose -f docker-compose.yml config`
  - `docker compose -f docker-compose.prod.yml config`
- Inside the Compose network, prefer referencing services by their service name (e.g., `postgres`) instead of `container_name`. Reserve `container_name` for operator tooling (e.g., `docker exec`).
- If any differences are intentional, document them briefly in `Guide-For-Admins-and-Tenants.md` so operators know what to expect.

## Development & Runtime Environment Parity
Maintain behavioral parity between development and deployed (compose / production-like) environments to reduce configuration drift and non-reproducible bugs:
- Derive externally visible URLs from a single configured PublicBaseUrl (fallback to GatewayBaseUrl) instead of recomputing per service with ad-hoc host rewrites.
- Avoid implicit host canonicalization (e.g., rewriting localhost -> 127.0.0.1) unless an explicit env var gate is provided; such rewrites can break token issuer matching and cookie scopes.
- Keep OIDC Authority, Issuer, and discovery MetadataAddress aligned. If path prefixes (e.g., gateway routing /identity, /dashboard) are used, apply them uniformly when registering redirect URIs and seeding clients.
- Prefer additive allowances (ValidIssuers collection including trailing-slash variations) over disabling issuer validation entirely during troubleshooting.
- Configuration freezes (e.g., static OIDC metadata) must be optional and environment-gated so that key rotation and JWKS refresh continue to work seamlessly in long-lived sessions.
- Any temporary diagnostics flags (signature bypass, issuer validation disable, forced preload) must be clearly named with DASHBOARD_* (or service-specific) prefixes and removed or off by default in committed code.

When adding new diagnostics or mitigations, ensure they either converge back to the production shape automatically or are disabled without code edits (env var toggles). This minimizes divergence and unexpected behavior gaps between local E2E tests and real deployments.

### OIDC configuration standard (dev/compose) — REQUIRED

Use one browser-visible issuer and route all backchannel discovery/JWKS via the in-cluster gateway. Keep this consistent across services and docker-compose so tokens validate uniformly and discovery works in containers.

- Identity (issuer authority)
  - Issuer (dev): http://127.0.0.1:8080/identity/ (note trailing slash).
  - Discovery must advertise /identity endpoints at the PublicBaseUrl host (127.0.0.1 in dev) so browsers see 127.0.0.1 URLs.

- Dashboard (OIDC client)
  - Oidc__Authority: http://127.0.0.1:8080/identity
  - Oidc__MetadataAddress: http://gateway:8080/identity/.well-known/openid-configuration (backchannel)
  - RequireHttpsMetadata=false in Development; true in Production.

- Database, Storage (JWT bearer validators)
  - Oidc__Issuer: http://127.0.0.1:8080/identity/
  - Oidc__MetadataAddress: http://gateway:8080/identity/.well-known/openid-configuration
  - Reason: tokens are issued with iss=http://127.0.0.1:8080/identity/ while services in containers must discover via gateway, not 127.0.0.1.

Compose env examples (dev):
- identity: Oidc__Issuer=http://127.0.0.1:8080/identity/
- dashboard: Oidc__Authority=http://127.0.0.1:8080/identity and Oidc__MetadataAddress=http://gateway:8080/identity/.well-known/openid-configuration
- db: Oidc__Issuer=http://127.0.0.1:8080/identity/ and Oidc__MetadataAddress=http://gateway:8080/identity/.well-known/openid-configuration
- storage: Oidc__Issuer=http://127.0.0.1:8080/identity/ and Oidc__MetadataAddress=http://gateway:8080/identity/.well-known/openid-configuration

### Program.cs implementation contract (services)

Apply the same startup pattern for services that validate JWTs (Database, Storage). Keep Dashboard’s OIDC client conformant to the same discovery logic.

- Common for Database/Storage (JwtBearer):
  - options.Authority: derive from Oidc:Issuer; store no trailing slash (TrimEnd('/')).
  - options.MetadataAddress resolution order:
    1) Use Oidc:MetadataAddress if set.
    2) Else, if DOTNET_RUNNING_IN_CONTAINER=true → http://gateway:8080/identity/.well-known/openid-configuration
    3) Else derive from Issuer: issuerWithSlash + ".well-known/openid-configuration"
  - Development:
    - RequireHttpsMetadata=false
    - MapInboundClaims=false
    - ValidIssuers must include both trailing-slash variants and loopback alternates (127.0.0.1 and localhost).
    - It’s acceptable to relax audience validation at the JWT layer and enforce via controller authorization policies.
  - TokenValidationParameters:
    - ValidTypes includes at+jwt and jwt/JWT.
    - ValidateIssuer=true; ValidateAudience as per environment (Dev may set false for service APIs).

- Dashboard (OIDC client):
  - Authority=http://127.0.0.1:8080/identity (browser-visible)
  - MetadataAddress=http://gateway:8080/identity/.well-known/openid-configuration (backchannel)
  - Prefer discovery; RequireHttpsMetadata=false in dev, true in prod.
  - Validate audience (client id) and issuer strictly in Production.

- Identity:
  - Always issue tokens with Issuer matching the browser-visible base (dev: http://127.0.0.1:8080/identity/).
  - Discovery must reflect the same issuer and expose JWKS at /identity/.well-known/jwks.

Production adjustments
- Use a single HTTPS PublicBaseUrl origin (e.g., https://apps.example.com/).
- Issuer: https://apps.example.com/identity/.
- Dashboard: Authority=https://apps.example.com/identity; MetadataAddress=https://apps.example.com/identity/.well-known/openid-configuration.
- Services: Oidc:Issuer=https://apps.example.com/identity/; allow MetadataAddress override only if strictly necessary.
- RequireHttpsMetadata=true; ValidateIssuer, ValidateAudience, and signing key validation must be enabled.

## App service URLs and OIDC behind Gateway (dev/compose)

Use one public base URL for all browser-visible links and OIDC values; keep it distinct from in-cluster URLs.

- Single source of truth
  - Prefer PublicBaseUrl; fallback to GatewayBaseUrl for browser-visible links.
  - In dev/E2E, use 127.0.0.1 rather than localhost to avoid IPv6/loopback divergence (tests assume http://127.0.0.1:8080).
  - Do not rewrite hosts implicitly (localhost → 127.0.0.1) unless gated by DASHBOARD_CANONICALIZE_LOOPBACK=1.

- Identity (OpenIddict)
  - Issuer (dev): http://127.0.0.1:8080/identity/ (note trailing slash).
  - Discovery must advertise /identity endpoints and JWKS at http://127.0.0.1:8080/identity/.well-known/jwks.
  - Inside Docker, services can reach Identity via http://gateway:8080/identity, but browsers should use 127.0.0.1.

- Dashboard (OIDC client)
  - Authority: http://gateway:8080/identity; MetadataAddress: http://gateway:8080/identity/.well-known/openid-configuration.
  - Prefer discovery. If fallback composes from Authority, ensure a trailing slash to avoid root-level path composition.
  - RequireHttpsMetadata=false in dev; AcceptAnyServerCert only when testing HTTPS in dev.
  - CallbackPath=/signin-oidc; SignedOutCallbackPath=/signout-callback-oidc. Gateway maps both root and /dashboard variants.
  - Build RedirectUri honoring X-Forwarded-Prefix when under /dashboard.
  - TokenValidationParameters: ValidateAudience=true with ValidAudience=client id; ValidIssuers includes both trailing-slash variants.

### Canonical Dashboard URLs (MUST)

- All Dashboard pages are exposed under the single public base path `/dashboard`.
- Admin routes MUST be addressed as `/dashboard/admin/*`.
- Tenant routes MUST be addressed as `/dashboard/tenant/*`.
- Root-level aliases like `/admin/*` or `/tenant/*` MUST NOT exist in the gateway or UI. Do not generate or rely on such links.
- The OIDC post-authentication redirect MUST preserve the `/dashboard` prefix. The Dashboard app SHALL persist the originally requested URL (absolute) at challenge time and force the return URL to that exact value on the OIDC callback.
- The gateway MUST set `X-Forwarded-Prefix: /dashboard` for Dashboard routes, but MUST NOT rewrite the browser-visible path away from `/dashboard`.

- Gateway routing
  - Canonical Identity base: /identity. Root aliases (/.well-known/*, /connect/*) exist for compatibility; clients should prefer /identity/*.
  - Set X-Forwarded-Proto=https and X-Forwarded-Prefix appropriately; preserve original Host.
  - Forward dashboard static, Blazor WS, and callbacks at root and /dashboard.

- Dev redirect URIs to register
  - http://127.0.0.1:8080/signin-oidc
  - http://127.0.0.1:8080/signout-callback-oidc
  - http://127.0.0.1:8080/dashboard/signin-oidc
  - http://127.0.0.1:8080/dashboard/signout-callback-oidc

- Playwright/E2E
  - Base URL: http://127.0.0.1:8080 and wait for /.well-known/openid-configuration.
  - A redirect to http://gateway:8080/connect/authorize is expected inside Docker; host browsers should follow 127.0.0.1 URLs from discovery.

- Dev cookie/proxy
  - Cookies: SameSite=Lax, Secure=None for correlation/nonce/auth cookies in HTTP dev.
  - Trust X-Forwarded-*; don’t mutate PathBase from X-Forwarded-Prefix at downstream apps (gateway strips prefixes).

- Diagnostic toggles (dev-only; default OFF)
  - DASHBOARD_BYPASS_IDTOKEN_SIGNATURE=1 to bypass id_token signature (SignatureValidator returns JsonWebToken and ValidateIssuerSigningKey=false).
  - DASHBOARD_DISABLE_SIGKEY_VALIDATION=1 to temporarily disable signing key validation.
  - DASHBOARD_FREEZE_OIDC_CONFIG=1 to freeze discovery/JWKS at startup.
  - DASHBOARD_CANONICALIZE_LOOPBACK=1 to opt-in localhost→127.0.0.1 canonicalization.

## App service URLs and OIDC behind Gateway (production)

Use one HTTPS public base URL for all browser-visible links and OIDC values; keep it distinct from in-cluster service URLs.

- Single source of truth
  - PublicBaseUrl must be an HTTPS origin, e.g., https://apps.example.com/.
  - Do not rewrite hosts implicitly; the configured PublicBaseUrl is authoritative for all externally visible links.
  - Terminate TLS at the gateway or upstream LB; ensure HTTP→HTTPS redirects and HSTS at the edge.

- Identity (OpenIddict)
  - Issuer: https://apps.example.com/identity/ (note trailing slash). This MUST match the discovery response and tokens.
  - Discovery must advertise endpoints and JWKS under /identity at the PublicBaseUrl.
  - Persist signing/encryption keys and allow JWKS refresh (no freezing by default).

- Dashboard (OIDC client)
  - Authority: https://apps.example.com/identity; MetadataAddress: https://apps.example.com/identity/.well-known/openid-configuration.
  - RequireHttpsMetadata=true; do not use AcceptAnyServerCert.
  - CallbackPath=/signin-oidc; SignedOutCallbackPath=/signout-callback-oidc. If Dashboard is served under a prefix (e.g., /dashboard), ensure RedirectUri honors the prefix via X-Forwarded-Prefix and that both prefixed and root URIs are registered only if both are actually exposed.
  - TokenValidationParameters:
    - ValidateIssuerSigningKey=true; ValidateAudience=true (ValidAudience=client id).
    - ValidateIssuer=true with a strict ValidIssuer matching the production issuer (include trailing-slash variant only if needed).

- Gateway routing
  - Prefer canonical /identity/* endpoints; remove public root aliases (/.well-known/*, /connect/*) unless a strong compatibility need exists.
  - Preserve Host; set X-Forwarded-Proto=https and X-Forwarded-Prefix where applicable.
  - Forward dashboard static, Blazor WS, and callbacks; configure sane timeouts and sticky sessions for WS.

- Redirect URIs to register (production examples)
  - https://apps.example.com/signin-oidc
  - https://apps.example.com/signout-callback-oidc
  - If exposed: https://apps.example.com/dashboard/signin-oidc and /dashboard/signout-callback-oidc

- Cookies and security headers
  - Cookies: Secure=Always; SameSite=Lax is typically safe for OIDC code flow; scope Path=/.
  - Enable HSTS at the edge; set appropriate CSP/CORS per app needs.

- Diagnostics and toggles
  - All dev diagnostics (bypass signature, disable key validation, freeze discovery, loopback canonicalization) must be OFF in production.
  - Rely on discovery/JWKS refresh and structured logs; do not relax validation in production.

## Dashboard Design Guidelines (MudBlazor Components)

### Overview
TansuCloud Dashboard uses **Blazor Server** with **MudBlazor** (MIT License) to provide a modern, professional, and consistent user experience. MudBlazor is a 100% open-source Material Design component library - no license keys or registration required. Follow these guidelines for all Dashboard UI development.

### Why MudBlazor
- ✅ **Open-Source**: MIT License, completely free for any use
- ✅ **No License Keys**: Zero registration or license management overhead
- ✅ **Contributor-Friendly**: Just `dotnet add package MudBlazor` and start coding
- ✅ **Production-Ready**: Used by Microsoft internally and thousands of projects
- ✅ **Material Design**: Modern, clean aesthetic aligned with Google's design system
- ✅ **Active Community**: 19k+ GitHub stars, excellent documentation and support
- ✅ **Feature-Complete**: 60+ components covering all common UI patterns

### Required NuGet Package
Only one package needed for the entire component library:
- **`MudBlazor`** (latest stable version for .NET 9)
  - Includes all components: grids, forms, navigation, charts, dialogs, etc.
  - No separate packages for individual components

Add to `TansuCloud.Dashboard.csproj`:
```bash
dotnet add package MudBlazor
```

### Configuration
**In `Program.cs`** (before `builder.Build()`):
```csharp
builder.Services.AddMudServices();
```

**In `App.razor`** or `_Host.cshtml` (in `<head>`):
```html
<link href="https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap" rel="stylesheet" />
<link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
<script src="_content/MudBlazor/MudBlazor.min.js"></script>
```

**Theme Customization** (optional, in `App.razor`):
```razor
<MudThemeProvider Theme="@_theme" />
<MudDialogProvider />
<MudSnackbarProvider />

@code {
    private MudTheme _theme = new()
    {
        Palette = new PaletteLight
        {
            Primary = "#594AE2",      // Brand purple
            Secondary = "#FF4081",     // Accent pink
            AppbarBackground = "#594AE2"
        }
    };
}
```

### Layout Structure (REQUIRED)

#### Left Sidebar Navigation
- Use **`MudDrawer`** component with collapsible behavior
- Fixed width: 240px expanded, 56px collapsed (mini variant with icons only)
- Position: Left side, full height
- Anchor: Start (left)
- **`MudNavMenu`** inside drawer for navigation items

**Basic Structure**:
```razor
<MudDrawer @bind-Open="@_drawerOpen" Elevation="1" Variant="@DrawerVariant.Mini" 
           OpenMiniOnHover="true" Width="240px" MiniWidth="56px">
    <MudDrawerHeader>
        <MudText Typo="Typo.h6">TansuCloud</MudText>
    </MudDrawerHeader>
    <MudNavMenu>
        <MudNavGroup Title="Dashboard" Icon="@Icons.Material.Filled.Dashboard">
            <MudNavLink Href="/admin" Icon="@Icons.Material.Filled.Home">Overview</MudNavLink>
        </MudNavGroup>
        <!-- More groups... -->
    </MudNavMenu>
</MudDrawer>
```

#### Menu Organization (Standard Groups)
Group related pages under these categories using **`MudNavGroup`**:

1. **Dashboard** (Overview/Home)
   - Icon: `Icons.Material.Filled.Dashboard`
   - System Overview
   - Quick Actions
   - Recent Activity

2. **Networking**
   - Icon: `Icons.Material.Filled.Network`
   - Domains/TLS
   - IP Policies (CORS, Allow/Deny)

3. **Routing**
   - Icon: `Icons.Material.Filled.Route`
   - YARP Routes
   - Health Probes

4. **Caching**
   - Icon: `Icons.Material.Filled.Storage`
   - Output Cache
   - Rate Limits
   - Cache/Rate Policies

5. **Identity**
   - Icon: `Icons.Material.Filled.People`
   - Users & Roles
   - Password Policies
   - Token Settings

6. **Storage & Database**
   - Icon: `Icons.Material.Filled.Database`
   - Buckets & Collections
   - Quotas & Lifecycle
   - Vector Settings

7. **Observability**
   - Icon: `Icons.Material.Filled.Visibility`
   - Metrics Dashboard (Task 36)
   - Retention Settings
   - Sampling & Alerts
   - Trace Viewer

8. **Policy Center**
   - Icon: `Icons.Material.Filled.Policy`
   - Policy List
   - Enforcement Modes
   - Simulators

9. **Tenants** (Admin only)
   - Icon: `Icons.Material.Filled.Business`
   - Tenant List
   - Provisioning
   - Configuration

10. **Settings**
    - Icon: `Icons.Material.Filled.Settings`
    - Profile
    - Preferences
    - Audit Log

#### Top Navigation Bar (AppBar)
- Use **`MudAppBar`** component
- Height: 64px (Material Design standard)
- Position: Fixed at top
- Components: Hamburger menu toggle, breadcrumb, spacer, user menu, notifications

**Basic Structure**:
```razor
<MudAppBar Elevation="1">
    <MudIconButton Icon="@Icons.Material.Filled.Menu" Color="Color.Inherit" Edge="Edge.Start" 
                   OnClick="@ToggleDrawer" />
    <MudBreadcrumbs Items="_breadcrumbs" />
    <MudSpacer />
    <MudIconButton Icon="@Icons.Material.Filled.Notifications" Color="Color.Inherit" />
    <MudMenu Icon="@Icons.Material.Filled.AccountCircle" Color="Color.Inherit">
        <MudMenuItem>Profile</MudMenuItem>
        <MudMenuItem>Logout</MudMenuItem>
    </MudMenu>
</MudAppBar>
```

### Component Selection Guide (By Use Case)

#### Data Display & Editing

**Grid/Table with CRUD**: **`MudTable<T>`** or **`MudDataGrid<T>`**
- Use `MudDataGrid` for advanced features (virtualization, grouping, aggregation)
- Use `MudTable` for simpler tables with less data
- Examples: Policy list, domain list, route list, user list
- Features: Built-in paging, sorting, filtering, row selection
- Toolbar: Use `ToolBarContent` with `MudButton` components

```razor
<MudDataGrid T="Policy" Items="@_policies" Filterable="true" SortMode="SortMode.Multiple"
             Groupable="true" Pageable="true" RowsPerPage="10">
    <ToolBarContent>
        <MudButton Color="Color.Primary" OnClick="@AddNew">Add Policy</MudButton>
        <MudSpacer />
        <MudTextField @bind-Value="_searchString" Placeholder="Search" Adornment="Adornment.Start"
                      AdornmentIcon="@Icons.Material.Filled.Search" IconSize="Size.Medium" />
    </ToolBarContent>
    <Columns>
        <PropertyColumn Property="x => x.Name" />
        <PropertyColumn Property="x => x.Type" />
        <TemplateColumn Title="Actions">
            <CellTemplate>
                <MudIconButton Icon="@Icons.Material.Filled.Edit" Size="Size.Small" OnClick="@(() => Edit(context.Item))" />
                <MudIconButton Icon="@Icons.Material.Filled.Delete" Size="Size.Small" OnClick="@(() => Delete(context.Item))" />
            </CellTemplate>
        </TemplateColumn>
    </Columns>
</MudDataGrid>
```

**Read-only Tables**: **`MudSimpleTable`** or **`MudTable<T>`** (read-only mode)
- Examples: Audit logs, health probe results, metrics tables
- Use `ReadOnly="true"` and disable editing

**Card Layout**: **`MudCard`** for dashboard summaries or item previews
- Examples: System overview, tenant cards, quick stats
- Structure: `MudCardHeader`, `MudCardContent`, `MudCardActions`

```razor
<MudCard Elevation="2">
    <MudCardHeader>
        <CardHeaderAvatar>
            <MudAvatar Color="Color.Primary">T</MudAvatar>
        </CardHeaderAvatar>
        <CardHeaderContent>
            <MudText Typo="Typo.h6">Active Tenants</MudText>
        </CardHeaderContent>
    </MudCardHeader>
    <MudCardContent>
        <MudText Typo="Typo.h3">42</MudText>
    </MudCardContent>
</MudCard>
```

**Tree View**: **`MudTreeView<T>`** for hierarchical data
- Examples: Route hierarchies, storage folder structure, tenant organization
- Supports icons, selection, and expand/collapse

**List**: **`MudList`** for simple lists with optional actions
- Use `MudListItem` with icons and secondary text
- Examples: Recent activity, notifications, quick links

#### Forms & Input

**Form Layout**: Wrap inputs in **`MudForm`** with **`MudGrid`** for responsive layout
- Use `@ref` to access form validation state
- FluentValidation integration available via `MudBlazor.FluentValidation` package

**Text Input**: **`MudTextField<T>`**
- Examples: Policy IDs, descriptions, names
- Props: `Label`, `Variant`, `Required`, `HelperText`, `Error`, `ErrorText`
- Use `InputType.Password` for sensitive fields

```razor
<MudTextField T="string" @bind-Value="_model.Name" Label="Policy Name" 
              Required="true" RequiredError="Name is required"
              Variant="Variant.Outlined" />
```

**Dropdowns**: **`MudSelect<T>`** or **`MudAutocomplete<T>`** (with search)
- Examples: Policy types, enforcement modes, tenant selection
- Use `MultiSelection="true"` for multiple selections (e.g., CORS origins)

**Numeric Input**: **`MudNumericField<T>`**
- Examples: TTL seconds, permits, window sizes, port numbers
- Props: `Min`, `Max`, `Step` for validation and increment/decrement

**Date/Time**: **`MudDatePicker`**, **`MudTimePicker`**, or combine both
- Examples: Certificate expiry, retention periods, scheduled tasks
- Material Design calendar/time picker with keyboard support

**Toggle/Switch**: **`MudSwitch<T>`** for boolean settings
- Examples: Enable/disable flags, feature toggles
- Use `Color` prop for visual emphasis

**Checkbox**: **`MudCheckBox<T>`** for boolean options
- Examples: Terms acceptance, feature opt-in
- Can be used in lists or forms

**Radio**: **`MudRadioGroup<T>`** with **`MudRadio`** for single selection
- Examples: Enforcement mode selection, policy type

**Slider**: **`MudSlider<T>`** for numeric ranges
- Examples: Retention days, sampling percentage

**Rich Text**: Use existing CodeMirror integration or integrate third-party editor
- MudBlazor doesn't include rich text editor (by design, to keep library focused)

**File Upload**: **`MudFileUpload<T>`**
- Examples: Certificate uploads, bulk imports
- Supports drag-drop and validation

#### Feedback & Messaging

**Toast Notifications**: **`MudSnackbar`** (via `ISnackbar` service injection)
- Position: Bottom-left (default), customizable
- Auto-dismiss after 3-5 seconds
- Variants: Success, Error, Warning, Info

```csharp
@inject ISnackbar Snackbar

// Usage
Snackbar.Add("Policy created successfully", Severity.Success);
Snackbar.Add("Failed to delete policy", Severity.Error);
```

**Dialogs/Modals**: **`MudDialog`** (via `IDialogService`)
- Examples: Create policy, delete confirmation, edit forms
- Full control over header, content, and actions

```csharp
@inject IDialogService DialogService

// Show confirmation
var result = await DialogService.ShowMessageBox(
    "Delete Policy", 
    "Are you sure you want to delete this policy?", 
    yesText: "Delete", cancelText: "Cancel");

// Show custom dialog
var dialog = await DialogService.ShowAsync<PolicyEditDialog>("Edit Policy", 
    new DialogParameters { ["Policy"] = policy });
var result = await dialog.Result;
```

**Loading Spinner**: **`MudProgressCircular`** during async operations
- Show when fetching data, saving changes, or running simulations
- Use `Indeterminate="true"` for unknown duration
- Overlay pattern: Wrap content in container with spinner overlay

**Progress Bar**: **`MudProgressLinear`** for long-running tasks
- Examples: Certificate rotation, bulk operations, imports
- Use `Value` prop for determinate progress (0-100)

**Alert**: **`MudAlert`** for inline messages
- Examples: Validation errors, warnings, info boxes
- Variants: Success, Error, Warning, Info
- Can be dismissible with close button

#### Data Visualization (Task 36 - Metrics Dashboards)

**Charts**: **`MudChart`** for data visualization
- Types: Line, Bar, Pie, Donut
- Examples: Metrics over time, resource usage, comparisons
- Simple API with data binding

```razor
<MudChart ChartType="ChartType.Line" ChartSeries="@_series" XAxisLabels="@_xAxisLabels"
          Width="100%" Height="300px" />
```

**For Advanced Charts**: Consider integrating:
- ApexCharts.Blazor (more chart types, interactive)
- Plotly.Blazor (scientific/statistical charts)
- ChartJs.Blazor (Chart.js wrapper)

MudChart covers basic needs; use specialized libraries for complex dashboards.

**Sparklines**: Use small **`MudChart`** instances with minimal styling
- Examples: Quick trends in tables, dashboard cards

**Gauges**: Use **`MudProgressCircular`** with custom styling or integrate third-party gauge library
- Examples: CPU usage, cache hit ratio, uptime percentage

#### Layout & Organization

**Tabs**: **`MudTabs`** with **`MudTabPanel`** for grouping related content
- Examples: Policy config tabs (Cache/Rate Limit), settings sections
- Keep tab count to 5-7 maximum per view
- Supports icons and badges

**Accordion**: **`MudExpansionPanels`** with **`MudExpansionPanel`** for collapsible sections
- Examples: Advanced settings, FAQ, help sections
- Can have multiple panels expanded or single (accordion mode)

**Container**: **`MudContainer`** for responsive content width
- MaxWidth options: xs, sm, md, lg, xl, xxl, false (full width)
- Centers content and applies consistent padding

**Paper**: **`MudPaper`** for elevated surfaces
- Use for grouping related content
- Elevation levels 0-24 (Material Design standard)

**Grid**: **`MudGrid`** with **`MudItem`** for responsive layouts
- 12-column grid system
- Breakpoint-specific sizing: xs, sm, md, lg, xl

```razor
<MudGrid>
    <MudItem xs="12" sm="6" md="4">
        <MudCard>Card 1</MudCard>
    </MudItem>
    <MudItem xs="12" sm="6" md="4">
        <MudCard>Card 2</MudCard>
    </MudItem>
</MudGrid>
```

**Divider**: **`MudDivider`** for visual separation
- Horizontal or vertical
- Use to separate sections

**Spacer**: **`MudSpacer`** to push elements apart (flexbox)
- Use in toolbars, headers, footers

### Styling Guidelines

#### Theme
- **Material Design 3** principles by default
- Define custom theme in `App.razor` via `MudThemeProvider`
- Customize: Primary, Secondary, Tertiary colors, typography, spacing
- Dark mode support built-in (toggle via `MudThemeProvider @bind-IsDarkMode`)

#### Consistency Rules
- **Spacing**: MudBlazor uses Material Design spacing scale (4px base unit)
  - Classes: `ma-*`, `pa-*`, `mt-*`, `pt-*` (where * = 0-16)
  - Examples: `class="pa-4"` (padding all sides, 16px), `class="mt-2"` (margin top, 8px)
- **Typography**: Use `Typo` enum on `MudText` component
  - h1-h6 for headings, body1-body2 for content, button, caption, etc.
- **Icons**: Material Design icons built-in via `@Icons.Material.Filled.*`, `@Icons.Material.Outlined.*`
  - No Font Awesome needed unless specific icons required
- **Buttons**: Use `MudButton` with `Variant` and `Color` props
  - Filled (`Variant.Filled`): Primary actions (Save, Create, Apply)
  - Outlined (`Variant.Outlined`): Secondary actions (Cancel, Back)
  - Text (`Variant.Text`): Tertiary actions (links, less emphasis)
  - Colors: Primary, Secondary, Error, Warning, Info, Success

#### Responsive Design
- Mobile-first: All MudBlazor components are responsive by default
- Breakpoints (Material Design): xs (0px), sm (600px), md (960px), lg (1280px), xl (1920px), xxl (2560px)
- Use `MudHidden` to show/hide elements at specific breakpoints
- Drawer: Use `Breakpoint="Breakpoint.Md"` to auto-close on mobile
- Grid: Use `xs`, `sm`, `md`, `lg`, `xl` props on `MudItem` for responsive sizing

### Accessibility (REQUIRED)
- MudBlazor components follow **WCAG 2.1 Level AA** guidelines by default
- Always provide:
  - `aria-label` for icon-only buttons (or use `Title` prop)
  - `alt` text for images (use `MudImage` with `Alt` prop)
  - Semantic HTML structure (headings, landmarks)
  - Keyboard navigation is built-in for all interactive components
  - Focus indicators are visible (Material Design focus ring)
- Test with screen readers (NVDA, JAWS) for critical flows
- Use proper heading hierarchy (h1 → h2 → h3, no skipping)

### Performance Best Practices
- **Virtualization**: Use `MudDataGrid` with `Virtualize="true"` for large datasets (>100 rows)
  - Renders only visible rows, improves performance dramatically
- **Lazy Loading**: Load tab content on demand using `@if` logic
- **Debouncing**: MudTextField has built-in `DebounceInterval` prop (default 300ms)
  - Use for search inputs to reduce API calls
- **Memoization**: Use `@key` directives to prevent unnecessary re-renders
- **SignalR Optimization**: Batch state updates in Blazor Server to reduce round-trips
  - MudBlazor is optimized for Blazor Server (fewer JS interop calls)
- **Elevation**: Lower elevation values (0-4) render faster than higher ones

### Error Handling & Validation
- Use `MudForm` with `@ref` to access validation state
- Display validation errors inline via `Error` and `ErrorText` props on inputs
- Show global errors via `MudAlert` or `MudSnackbar`
- Use ProblemDetails responses from APIs and map to user-friendly messages
- Provide clear error messages with action guidance (e.g., "Invalid TTL. Must be between 60 and 86400 seconds.")

### Common Patterns

#### CRUD Page Pattern
1. **`MudDataGrid`** with toolbar (Add/Edit/Delete/Search)
2. **Add/Edit**: Open `MudDialog` with form (`MudForm` + input components)
3. **Delete**: Show confirmation via `DialogService.ShowMessageBox`
4. **Save**: Call API, show `MudSnackbar` with result, refresh grid
5. **Cancel**: Close dialog without saving

#### Simulator Pattern (Cache/Rate Limit Simulators)
1. **`MudPaper`** or **`MudCard`** with form inputs (`MudTextField`, `MudSelect`, etc.)
2. **"Simulate" button** (`MudButton`) triggers API call
3. Display results in **`MudExpansionPanel`** or **`MudDialog`**
4. Show before/after comparison in **`MudGrid`** with two columns
5. **Copy button** (`MudIconButton` with `Icons.Material.Filled.ContentCopy`) to copy results

#### Dashboard Pattern (Overview Pages)
1. **`MudGrid`** with **`MudCard`** components for KPIs (3-4 per row on desktop)
   - Use `xs="12" sm="6" md="4" lg="3"` for responsive cards
2. **`MudChart`** for trends and comparisons
3. **`MudButton`** group for quick actions (Create, Import, Settings)
4. **`MudTable`** or **`MudList`** for recent activity (last 10 items)
5. Use **`MudSkeleton`** for loading states

### Migration Strategy for Existing Pages
When updating an existing page to use MudBlazor components:
1. **Identify current functionality** (CRUD, display, form, etc.)
2. **Map to appropriate MudBlazor components** using this guide
3. **Preserve existing API calls and business logic** (only change UI layer)
4. **Replace HTML/Bootstrap with MudBlazor components**:
   - `<table>` → `MudTable` or `MudDataGrid`
   - `<input>` → `MudTextField`, `MudNumericField`, etc.
   - `<button>` → `MudButton`
   - `<div class="card">` → `MudCard`
5. **Test thoroughly** (keyboard nav, responsive, accessibility)
6. **Update E2E tests** to use MudBlazor-specific selectors (data attributes, aria labels)

### Documentation & Examples
- **MudBlazor Docs**: https://mudblazor.com/
- **API Reference**: https://mudblazor.com/api
- **GitHub**: https://github.com/MudBlazor/MudBlazor
- **Discord Community**: https://discord.gg/mudblazor
- For each page, add inline comments referencing the MudBlazor component used and why

### Common Gotchas & Tips
- **Dialogs**: Must add `<MudDialogProvider />` in `App.razor` to use `IDialogService`
- **Snackbars**: Must add `<MudSnackbarProvider />` in `App.razor` to use `ISnackbar`
- **Icons**: Import `@using MudBlazor` in `_Imports.razor` for icon access
- **Forms**: Use `@ref` on `MudForm` to call `.Validate()` manually if needed
- **Theming**: Changes to `MudTheme` require page refresh (design-time only)
- **Color Prop**: Use `Color.Primary`, `Color.Secondary`, `Color.Error`, etc. (not CSS classes)

### Future Design Updates
When creating new Dashboard pages or updating existing ones:
- Always consult this guide first
- Use MudBlazor components exclusively (avoid mixing with Bootstrap or other UI libraries)
- Maintain consistency with existing navigation structure and styling
- Add new menu items to appropriate `MudNavGroup` sections
- Document any new patterns or component combinations in this guide
- Update E2E tests to cover MudBlazor-specific behaviors (e.g., dialog interactions, snackbar assertions)

## Service conventions: Database API v1

- Tenancy
  - Resolve tenant from `X-Tansu-Tenant` and normalize database names: non-alphanumeric → `_`, prefixed with `tansu_tenant_`. Keep runtime normalization identical to the provisioner.
  - Development-only diagnostic header `X-Tansu-Db` may be added by the Database service to surface the normalized DB name. Do not emit this header outside Development/E2E.

- Auth and JWT
  - Scopes: `db.read` for reads, `db.write` for writes. In Development, `admin.full` implies both.
  - Prefer `MapInboundClaims=false`. Accept tokens with `typ` of `at+jwt` (configure `ValidTypes`). In Development it’s acceptable to disable audience validation to simplify local runs.

- JSON handling for documents
  - Accept request `content` as `JsonElement?` and store as `JsonDocument?` mapped to PostgreSQL `jsonb` to avoid serialization pitfalls. Convert back to `JsonElement` for responses.
  - Use `System.Text.Json`; avoid double buffering and unnecessary allocations on hot paths.

- ETags and conditional requests
  - Return weak ETags for lists and items. Support `If-None-Match` → 304 for GET/list and `If-Match` → 412 for PUT/DELETE on mismatch.
  - Keep ETag computation stable and cheap; prefer a hash over immutable fields or a rowversion surrogate.

- Vector search (pgvector)
  - Embedding size is 1536. Create HNSW indexes in migrations when the `vector` extension is available; fall back gracefully otherwise.
  - Provide collection-scoped KNN and an optional cross-collection ANN endpoint with a reasonable default limit.

- Testing
  - Add focused tests for ETag behaviors (304/412), filters/sorting, and vector search happy path + 1 edge case.

## Standard dev database: Citus PostgreSQL (with pgvector) via a single named container

Use one persistent, repeatable Citus PostgreSQL container across dev sessions. We use a custom image that includes pgvector so init scripts can `CREATE EXTENSION vector` reliably.

- Container name: `tansudbpg`
- Named volume: `tansu-pgdata` (stores PGDATA; persists across runs)
- Image (dev default): `tansu/citus-pgvector:local` (built from `dev/Dockerfile.citus-pgvector`)
- Init scripts: keep idempotent SQL in repo and mount to `/docker-entrypoint-initdb.d`
  - Enable extensions: `CREATE EXTENSION IF NOT EXISTS citus;`, `vector`, `pg_trgm`
  - Prefer idempotent DDL patterns (IF NOT EXISTS or DO blocks) for roles/dbs
- Credentials/config: don’t hardcode secrets in code; use env or user secrets. For local-only convenience, you may set `POSTGRES_HOST_AUTH_METHOD=trust` (dev only).

Dev workflows (no Aspire)
- Start with Docker (Windows PowerShell shown; adjust paths for your OS):
  - Create/run container with persistent volume and init scripts mounted from the repo:
    - Image: `tansu/citus-pgvector:local` (or your pushed tag like `yourorg/citus-pgvector:pg17`)
    - Name: `tansudbpg`
    - Ports: map host 5432 to container 5432. If 5432 is occupied, use an alternate host port like 55432.
    - Volumes:
      - `tansu-pgdata:/var/lib/postgresql/data`
      - `<repo-root>/dev/db-init:/docker-entrypoint-initdb.d:ro`
    - Env (dev only): `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres` (or `POSTGRES_HOST_AUTH_METHOD=trust`)
- Stop: stop the `tansudbpg` container when you don’t need it.
- Persisted data: DB state is preserved via the `tansu-pgdata` named volume.
- Reset DB: stop the container and remove the `tansu-pgdata` volume, then start again; init scripts will re-seed on first run.
- Port conflicts: if another Postgres is bound to 5432, choose a different host port (e.g., `-p 55432:5432`) and point apps to that host port.

Notes
- Initialization scripts live at `<repo-root>/dev/db-init` and are mounted to `/docker-entrypoint-initdb.d` inside the container; keep them idempotent.
- Default local connection string example: `Host=localhost;Port=5432;Database=tansu_identity;Username=postgres;Password=postgres` (adjust the port if you remap).
- The container is for development only. Don’t expose it publicly and don’t reuse the dev credentials anywhere else.

Production (self-hosted Postgres/Citus)
- Use the same custom image, but pin a versioned tag (e.g., `yourorg/citus-pgvector:pg17`) and distribute it via a registry (Docker Hub/GHCR/ACR).
- Build in CI, scan (Trivy), sign (cosign), and deploy the same image to all Citus nodes (coordinator/workers).
- Don’t rely on `/docker-entrypoint-initdb.d` for schema in prod; use explicit SQL/provisioning and EF migrations.

Image build & publish (summary)
- Local build: `docker build -f dev/Dockerfile.citus-pgvector -t yourorg/citus-pgvector:pg17 .`
- Push: `docker push yourorg/citus-pgvector:pg17`
- A minimal CI workflow exists at `.github/workflows/build-citus-pgvector.yml` and expects `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` secrets.

## Run all services locally (VS Code tasks)

- Start all services:
  - VS Code: Terminal → Run Task… → "dev: up"
- Stop them:
  - VS Code: Terminal → Run Task… → "dev: down"

## Development sign-in credentials (dev-only)

For local development and E2E validation, the Identity service seeds a default admin user. Use these credentials when prompted:

- Email: admin@tansu.local
- Password: Passw0rd!

- SigNoz Web UI:   http://127.0.0.1:3301 Account name: musagursoy@yahoo.com Password: Dom57111222%

Notes
- These values come from `Seed:AdminEmail` and `Seed:AdminPassword` configuration keys (see `TansuCloud.Identity/Infrastructure/DevSeeder.cs`). You can override them via environment variables or appsettings for your environment without code changes.
- Do not use these credentials outside of development. They are intended solely for local runs and automated tests.