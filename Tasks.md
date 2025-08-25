# Tasks: TansuCloud (Initial Plan)

<!-- markdownlint-disable MD029 -->

Legend: [Svc] = service, [Role] = Admin/Tenant, Outcome = measurable deliverable.

## Quick checklists

### M1: Gateway + Identity

- [ ] [1) Bootstrap YARP gateway](./Tasks-M1.md#task-1-bootstrap-yarp-gateway)
- [ ] [2) Tenant resolution in Gateway](./Tasks-M1.md#task-2-tenant-resolution-in-gateway)
- [ ] [3) Safety controls at gateway](./Tasks-M1.md#task-3-safety-controls-at-gateway)
- [ ] [4) Identity server skeleton](./Tasks-M1.md#task-4-identity-server-skeleton)
- [ ] [5) Token claims and scopes](./Tasks-M1.md#task-5-token-claims-and-scopes)
- [ ] [6) Identity features baseline](./Tasks-M1.md#task-6-identity-features-baseline)
- [ ] [7) Dashboard auth integration (Blazor Server)](./Tasks-M1.md#task-7-dashboard-auth-integration)
- [ ] [8) OTEL baseline across services](./Tasks-M1.md#task-8-otel-baseline-across-services)

### M2: Database + Storage core

- [ ] [9) Tenant provisioning service](./Tasks-M2.md#task-9-tenant-provisioning-service)
- [ ] [10) EF Core model + migrations with Citus/pgvector](./Tasks-M2.md#task-10-ef-core-model--migrations)
- [ ] [11) Database REST API v1](./Tasks-M2.md#task-11-database-rest-api-v1)
- [ ] [12) Outbox + idempotency worker](./Tasks-M2.md#task-12-outbox--idempotency-worker)
- [ ] [13) Storage service core (S3-compatible)](./Tasks-M2.md#task-13-storage-service-core)
- [ ] [14) Storage compression and transforms (optional)](./Tasks-M2.md#task-14-storage-compression-and-transforms)
- [ ] [15) HybridCache integration](./Tasks-M2.md#task-15-hybridcache-integration)
- [ ] [16) Gateway OutputCache/rate limits tuning](./Tasks-M2.md#task-16-gateway-outputcache--rate-limits-tuning)

### M3: Dashboard + SDK

- [ ] [17) Dashboard admin surfaces](./Tasks-M3.md#task-17-dashboard-admin-surfaces)
- [ ] [18) Dashboard tenant manager surfaces](./Tasks-M3.md#task-18-dashboard-tenant-manager-surfaces)
- [ ] [19) Observability pages](./Tasks-M3.md#task-19-observability-pages)
- [ ] [20) Background jobs UX](./Tasks-M3.md#task-20-background-jobs-ux)
- [ ] [21) .NET SDK (NuGet) alpha](./Tasks-M3.md#task-21-dotnet-sdk-alpha)
- [ ] [22) CLI tool (alpha)](./Tasks-M3.md#task-22-cli-tool-alpha)

### M4: Experience enhancements and governance

- [ ] [23) Policy Center full](./Tasks-M4.md#task-23-policy-center-full)
- [ ] [24) Secrets scanner](./Tasks-M4.md#task-24-secrets-scanner)
- [ ] [25) Feature flags and chaos](./Tasks-M4.md#task-25-feature-flags-and-chaos)
- [ ] [26) Cost/usage explorer](./Tasks-M4.md#task-26-costusage-explorer)
- [ ] [27) Blueprints and preview environments](./Tasks-M4.md#task-27-blueprints-and-preview-environments)
- [ ] [28) Tenant as code (YAML)](./Tasks-M4.md#task-28-tenant-as-code)
- [ ] [29) Performance insights](./Tasks-M4.md#task-29-performance-insights)
- [ ] [30) Gateway tools](./Tasks-M4.md#task-30-gateway-tools)
- [ ] [31) Security and audit logging](./Tasks-M4.md#task-31-security-and-audit-logging)
- [ ] [32) Health endpoints + startup probes validation](./Tasks-M4.md#task-32-health-endpoints--startup-probes-validation)
- [ ] [33) CI build/test, containerization, compose](./Tasks-M4.md#task-33-ci-buildtest-containerization-compose)

Phase M1: Gateway + Identity (enable Dashboard login) — see [Tasks-M1](./Tasks-M1.md)

1) [Bootstrap YARP gateway](./Tasks-M1.md#task-1-bootstrap-yarp-gateway)
   - Outcome: Gateway routes /dashboard, /identity, /db, /storage; WebSockets enabled; tracing headers forwarded; TLS termination in dev.
   - Dependencies: None
2) [Tenant resolution in Gateway](./Tasks-M1.md#task-2-tenant-resolution-in-gateway)
   - Outcome: Subdomain and /t/{tenant} path parsing; sets X-Tansu-Tenant; unit tests for parser.
   - Dependencies: Task 1
3) [Safety controls at gateway](./Tasks-M1.md#task-3-safety-controls-at-gateway)
   - Outcome: Per-route auth policies, rate limiting, request body size limits; OutputCache policies (vary-by tenant/path/query/headers; bypass when Authorization unless public).
   - Dependencies: Tasks 1-2
4) [Identity server skeleton (OpenIddict + ASP.NET Identity)](./Tasks-M1.md#task-4-identity-server-skeleton)
   - Outcome: Discovery document, Auth Code PKCE login via Razor Pages; PostgreSQL persistence.
   - Dependencies: None
5) [Token claims and scopes](./Tasks-M1.md#task-5-token-claims-and-scopes)
   - Outcome: Tokens contain tid, roles, scopes (db.*, storage.*, admin.*); optional plan/quotas; policies mapped and documented.
   - Dependencies: Task 4
6) [Identity features baseline](./Tasks-M1.md#task-6-identity-features-baseline)
   - Outcome: MFA policy switches; JWKS rotation job; external OIDC registration per tenant (if enabled); audit log for security events; impersonation endpoints UI/APIs.
   - Dependencies: Task 4-5
7) [Dashboard auth integration (Blazor Server)](./Tasks-M1.md#task-7-dashboard-auth-integration)
   - Outcome: Dashboard authenticates via OIDC through gateway; Server render mode; SignalR via gateway; optional sticky sessions.
   - Dependencies: Tasks 1-6
8) [OTEL baseline across services](./Tasks-M1.md#task-8-otel-baseline-across-services)
   - Outcome: Traces/metrics/logs exported to shared collector; resource attributes set; health endpoints live/ready implemented.
   - Dependencies: 1,4,7

Phase M2: Database + Storage core — see [Tasks-M2](./Tasks-M2.md)
9) [Tenant provisioning service](./Tasks-M2.md#task-9-tenant-provisioning-service)
    - Outcome: Create tenant -> template DB init (Citus+pgvector) -> migrations -> seed roles/config -> admin invite -> audit event.
    - Dependencies: 4-5

10) [EF Core model + migrations with Citus/pgvector](./Tasks-M2.md#task-10-ef-core-model--migrations)
    - Outcome: Distributed/reference tables; HNSW indexes; deterministic migrations; compiled models.
    - Dependencies: 9
11) [Database REST API v1](./Tasks-M2.md#task-11-database-rest-api-v1)
    - Outcome: CRUD with pagination/filter/sort/ETag; validation; problem+json errors; vector upsert/search requiring collection_id; two-step cross-collection ANN path.
    - Dependencies: 10
12) [Outbox + idempotency worker](./Tasks-M2.md#task-12-outbox--idempotency-worker)
    - Outcome: Outbox table, background dispatcher with retries and idempotency keys; Redis pub/sub for cache busts.
    - Dependencies: 11
13) [Storage service core (S3-compatible)](./Tasks-M2.md#task-13-storage-service-core)
    - Outcome: Buckets, object CRUD, presigned URLs, multipart; quotas; lifecycle scaffolding; content-type validation; optional AV hook.
    - Dependencies: 9
14) [Storage compression and transforms (optional)](./Tasks-M2.md#task-14-storage-compression-and-transforms)
    - Outcome: Brotli for compressible types (respect Accept-Encoding; set Content-Encoding; preserve weak ETags; skip already-compressed); image transforms with signed URLs and per-tenant cache keyed by source ETag.
    - Dependencies: 13
15) [HybridCache integration](./Tasks-M2.md#task-15-hybridcache-integration)
    - Outcome: Redis configured; cache key conventions; cached endpoints; invalidation via outbox.
    - Dependencies: 11,13,12
16) [Gateway OutputCache/rate limits tuning](./Tasks-M2.md#task-16-gateway-outputcache--rate-limits-tuning)
    - Outcome: Per-route policies refined; public routes flagged; test vary keys; conditional requests (ETag/Last-Modified) honored end-to-end.
    - Dependencies: 3,11,13

Phase M3: Dashboard + SDK — see [Tasks-M3](./Tasks-M3.md)
17) [Dashboard admin surfaces](./Tasks-M3.md#task-17-dashboard-admin-surfaces)
    - Outcome: Instance admin pages: domains/TLS, YARP routes, output cache, rate limits, identity policies, DB/storage defaults, observability (retention/alerts/sampling/PII redaction), Policy Center (CORS/IP allow/deny).
    - Dependencies: 3,6,9,13,16
18) [Dashboard tenant manager surfaces](./Tasks-M3.md#task-18-dashboard-tenant-manager-surfaces)
    - Outcome: Tenant pages: profile/domains, API keys, users/roles, buckets/lifecycle/quotas, DB collections/vector settings, webhooks, per-tenant rate/cache policies; transform presets; retention simulator.
    - Dependencies: 11,13,15,14
19) [Observability pages](./Tasks-M3.md#task-19-observability-pages)
    - Outcome: Metrics/logs/traces for admin and tenant, scoped by tid; no direct backend exposure; live updates via SignalR; live log tail; "Why slow?" waterfall; error drill-down.
    - Dependencies: 8
20) [Background jobs UX](./Tasks-M3.md#task-20-background-jobs-ux)
    - Outcome: Long-running operations surface progress notifications; job history with audit links; canary checks and rollback UI.
    - Dependencies: 17-19
21) [.NET SDK (NuGet) alpha](./Tasks-M3.md#task-21-dotnet-sdk-alpha)
    - Outcome: AddTansuCloud(...) DI, typed clients from OpenAPI, auth handlers, retries (Polly), OTEL; token acquisition/refresh; tenant selection; cancellation.
    - Dependencies: 11,13
22) [CLI tool (alpha)](./Tasks-M3.md#task-22-cli-tool-alpha)
    - Outcome: CLI for tenant ops (provision/clone/export/import YAML), observability queries, blueprint apply, synthetic monitor manage.
    - Dependencies: 21,17,18

Phase M4: Experience enhancements and governance — see [Tasks-M4](./Tasks-M4.md)
23) [Policy Center full](./Tasks-M4.md#task-23-policy-center-full)
    - Outcome: Centralized CORS, IP allow/deny, rate limits, export/audit policies with staged rollout and audit.
    - Dependencies: 17
24) [Secrets scanner](./Tasks-M4.md#task-24-secrets-scanner)
    - Outcome: Secret scanner integrated in Dashboard and CI with redacted reports and remediation.
    - Dependencies: 24
25) [Feature flags and chaos](./Tasks-M4.md#task-25-feature-flags-and-chaos)
    - Outcome: Global/per-tenant flags with targeting and gradual rollout; chaos toggles in preview envs.
    - Dependencies: 17
26) [Cost/usage explorer](./Tasks-M4.md#task-26-costusage-explorer)
    - Outcome: Per-tenant cost/usage charts (storage GB, egress, DB QPS, vector queries, cache hit rate); CSV/JSON export.
    - Dependencies: 19
27) [Blueprints and preview environments](./Tasks-M4.md#task-27-blueprints-and-preview-environments)
    - Outcome: Blueprints to pre-provision resources/policies; ephemeral tenant clones with TTL and masked data.
    - Dependencies: 9,17,22
28) [Tenant as code (YAML)](./Tasks-M4.md#task-28-tenant-as-code)
    - Outcome: Export/import with idempotency and drift detection; validations and dry-run.
    - Dependencies: 22,27
29) [Performance insights](./Tasks-M4.md#task-29-performance-insights)
    - Outcome: Slow query analysis, vector index tuning hints, cache heatmaps and TTL suggestions.
    - Dependencies: 11,15,19
30) [Gateway tools](./Tasks-M4.md#task-30-gateway-tools)
    - Outcome: Response cache policy tester UI; synthetic monitors with alerting thresholds per route/tenant.
    - Dependencies: 16,19

Quality and operations
31) [Security and audit logging](./Tasks-M4.md#task-31-security-and-audit-logging)
    - Outcome: Central audit trail for admin/tenant actions; impersonation logs; retention settings; export policies.
    - Dependencies: 6,17,18
32) [Health endpoints + startup probes validation](./Tasks-M4.md#task-32-health-endpoints--startup-probes-validation)
    - Outcome: /health/live and /health/ready across services; docker-compose integration.
    - Dependencies: 8
33) [CI build/test, containerization, compose](./Tasks-M4.md#task-33-ci-buildtest-containerization-compose)
    - Outcome: Build pipelines, unit/integration tests, docker images, docker-compose for local dev (PostgreSQL+Citus, PgCat, Redis, OTEL, optional telemetry backends).
    - Dependencies: 1-30

Acceptance checkpoints

- [ ] AC-M1: Dashboard login through gateway using OIDC; tid propagated; traces visible; gateway safety controls in place.
- [ ] AC-M2: Database and Storage core APIs functional with tenant isolation, caching, and optional compression/transforms.
- [ ] AC-M3: Dashboard management and SDK clients operating against v1 APIs; observability working; background jobs visible.
- [ ] AC-M4: Governance/policy center, feature flags, gateway tools, insights, CLI/blueprints/tenant-as-code, cost/usage explorer.

<!-- markdownlint-enable MD029 -->
