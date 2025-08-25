# Phase M1: Gateway + Identity (enable Dashboard login)

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [1) Bootstrap YARP gateway](#task-1-bootstrap-yarp-gateway)
- [ ] [2) Tenant resolution in Gateway](#task-2-tenant-resolution-in-gateway)
- [ ] [3) Safety controls at gateway](#task-3-safety-controls-at-gateway)
- [ ] [4) Identity server skeleton](#task-4-identity-server-skeleton)
- [ ] [5) Token claims and scopes](#task-5-token-claims-and-scopes)
- [ ] [6) Identity features baseline](#task-6-identity-features-baseline)
- [ ] [7) Dashboard auth integration (Blazor Server)](#task-7-dashboard-auth-integration)
- [ ] [8) OTEL baseline across services](#task-8-otel-baseline-across-services)

---

## Tasks

<a id="task-1-bootstrap-yarp-gateway"></a>

### Task 1: Bootstrap YARP gateway

- Outcome: Gateway routes /dashboard, /identity, /db, /storage; WebSockets enabled; tracing headers forwarded; TLS termination in dev.
- Dependencies: None

<a id="task-2-tenant-resolution-in-gateway"></a>

### Task 2: Tenant resolution in Gateway

- Outcome: Subdomain and /t/{tenant} path parsing; sets X-Tansu-Tenant; unit tests for parser.
- Dependencies: Task 1

<a id="task-3-safety-controls-at-gateway"></a>

### Task 3: Safety controls at gateway

- Outcome: Per-route auth policies, rate limiting, request body size limits; OutputCache policies (vary-by tenant/path/query/headers; bypass when Authorization unless public).
- Dependencies: Tasks 1-2

<a id="task-4-identity-server-skeleton"></a>

### Task 4: Identity server skeleton

- Outcome: Discovery document, Auth Code PKCE login via Razor Pages; PostgreSQL persistence. (OpenIddict + ASP.NET Identity)
- Dependencies: None

<a id="task-5-token-claims-and-scopes"></a>

### Task 5: Token claims and scopes

- Outcome: Tokens contain tid, roles, scopes (db.*, storage.*, admin.*); optional plan/quotas; policies mapped and documented.
- Dependencies: Task 4

<a id="task-6-identity-features-baseline"></a>

### Task 6: Identity features baseline

- Outcome: MFA policy switches; JWKS rotation job; external OIDC registration per tenant (if enabled); audit log for security events; impersonation endpoints UI/APIs.
- Dependencies: Task 4-5

<a id="task-7-dashboard-auth-integration"></a>

### Task 7: Dashboard auth integration (Blazor Server)

- Outcome: Dashboard authenticates via OIDC through gateway; Server render mode; SignalR via gateway; optional sticky sessions.
- Dependencies: Tasks 1-6

<a id="task-8-otel-baseline-across-services"></a>

### Task 8: OTEL baseline across services

- Outcome: Traces/metrics/logs exported to shared collector; resource attributes set; health endpoints live/ready implemented.
- Dependencies: 1,4,7

---

### Checklist item template

- [ ] <Task number>) <Task title>
  - Owner:
  - Status: Not Started | In Progress | Blocked | Done
  - Start:  YYYY-MM-DD   Due: YYYY-MM-DD
  - Notes:

<!-- markdownlint-enable MD029 MD033 -->
