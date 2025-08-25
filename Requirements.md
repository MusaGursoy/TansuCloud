
# Requirements: TansuCloud (Initial)

This document captures initial functional and non-functional requirements using EARS format and acceptance criteria. Roles: Instance Admin (platform owner) and Tenant Manager (per-tenant owner).

## Sections

- A) Global requirements (apply to all services)
- B) Instance Admin requirements
- C) Tenant Manager requirements
- D) Service contracts (Identity, Gateway, Database, Storage, Dashboard, SDK)
- E) Experience enhancements and governance (backlog/optional)

## A. Global requirements

- WHEN any request traverses the system — THE SYSTEM SHALL preserve and propagate tenant context using header `X-Tansu-Tenant` and `tid` claim in tokens.
  
  Acceptance
  - Requests reaching downstream services include `X-Tansu-Tenant` when a tenant is resolved.
  - Issued access tokens include `tid` claim and services reject requests missing tenant context unless explicitly public.

- WHEN a user or service is authenticated — THE SYSTEM SHALL authorize using roles and scopes (db.read/write, storage.read/write, admin.*).
  
  Acceptance
  - Authorization policies map to scopes and roles, enforced per route in every service.

- WHEN operating in multi-tenant mode — THE SYSTEM SHALL isolate tenant data at the database level (database-per-tenant by default) with optional RLS within each tenant DB.
  
  Acceptance
  - Data created by one `tid` is not readable via APIs when using another `tid`.

- WHEN services emit telemetry — THE SYSTEM SHALL attach `tid`, user, and route attributes and export via OpenTelemetry to the shared collector.
  
  Acceptance
  - Traces/metrics/logs show `service.name` and `tid`; Dashboard can query only permitted `tid`s.

- WHEN cache is used — THE SYSTEM SHALL use HybridCache with Redis backing where configured and key names prefixed with tenant (`t:{tid}:...`).
  
  Acceptance
  - Keys observed in Redis are tenant-scoped; cache can be safely cleared per tenant.

- WHEN processing GET requests marked cacheable — THE SYSTEM SHALL support ETag/Last-Modified and gateway OutputCache with vary-by tenant, path, query, and selected headers, bypassing when Authorization is present unless the route is public.
  
  Acceptance
  - 304 responses are returned on conditional requests; OutputCache entries include tenant in the vary key.

- WHEN services start — THE SYSTEM SHALL expose health endpoints (liveness/readiness) and fail fast on missing critical dependencies.
  
  Acceptance
  - /health/live and /health/ready reflect dependency status and integrate with container probes.

- WHEN defining cache policy guidance — THE SYSTEM SHALL document what to cache per service and default TTLs.
  
  Acceptance
  - Guidance lists per-service items: Identity (discovery/JWKS), Database (tenant metadata/lookups), Storage (object/bucket metadata lists), Gateway (safe GET responses), Dashboard (ephemeral UI only).

## B. Instance Admin requirements

### Identity and access

- WHEN configuring identity — THE SYSTEM SHALL allow setting password/MFA policies, token lifetimes, and JWKS key rotation.
  
  Acceptance
  - Policy changes take effect on new authentications; rotation publishes new JWKS while honoring previous keys until expiry.

- WHEN registering external IdPs — THE SYSTEM SHALL allow OIDC providers to be configured per tenant (if enabled by admin policy).
  
  Acceptance
  - A tenant may enable/disable external login; sign-in flows complete and users are bound to the tenant.

- WHEN enabling impersonation — THE SYSTEM SHALL allow privileged admins to impersonate a user within a tenant with reason/time limit and full audit.
  
  Acceptance
  - Impersonation sessions are time?boxed; all actions are logged and attributable to the impersonator and target.

- WHEN enriching tokens — THE SYSTEM SHALL optionally include plan/quotas claims in tokens if configured.
  
  Acceptance
  - Tokens reflect configured plan/quotas for rate/limit enforcement.

### Tenants and provisioning

- WHEN creating a new tenant — THE SYSTEM SHALL provision a dedicated database from a template with Citus and pgvector enabled and seed default roles/configuration.
  
  Acceptance
  - Tenant appears in Directory; DB is created; migrations applied; default admin invite issued.

- WHEN assigning domains — THE SYSTEM SHALL support subdomain per tenant and optional custom domains with TLS.
  
  Acceptance
  - Gateway routes {tenant}.example.local to services with correct X-Tansu-Tenant; custom domains validate and bind certificates.

### Gateway and policies

- WHEN defining gateway routes — THE SYSTEM SHALL configure YARP for /dashboard, /identity, /db, /storage; enable WebSockets/SignalR; and optional sticky sessions.
  
  Acceptance
  - Blazor Server circuits function via gateway; routes are reachable; sticky sessions can be toggled.

- WHEN configuring safety controls — THE SYSTEM SHALL support per-route rate limiting, request body size limits, and OutputCache policies.
  
  Acceptance
  - Limits block excess traffic; oversize bodies are rejected; cache rules bypass when Authorization is present unless marked public.

### Storage and data

- WHEN defining storage defaults — THE SYSTEM SHALL allow global lifecycle rules, server-side encryption defaults, AV scan hook toggle, and Brotli policy.
  
  Acceptance
  - Objects follow lifecycle transitions; encryption headers are applied by default; Brotli honors Accept-Encoding and preserves weak ETags; is disabled for already-compressed media.

- WHEN defining database defaults — THE SYSTEM SHALL manage template DB settings including distribution keys and migration ordering.
  
  Acceptance
  - New tenants inherit configured distribution keys; create_distributed_table is applied deterministically.

### Observability and governance

- WHEN viewing system health — THE SYSTEM SHALL present instance-level metrics, logs, and traces with tenant filtering in the Dashboard.
  
  Acceptance
  - Admin can filter by `tid`; backends are not directly exposed to public networks.

- WHEN auditing actions — THE SYSTEM SHALL record admin actions and security events with actor, time, tenant, and rationale.
  
  Acceptance
  - Immutable audit entries exist for tenant provisioning, policy changes, and impersonation.

- WHEN configuring observability — THE SYSTEM SHALL allow retention, alert rules, sampling, and PII redaction policies to be managed in Dashboard.
  
  Acceptance
  - Policy changes propagate to the collector/backends and are enforced.

- WHEN integrating billing — THE SYSTEM SHALL provide hooks for usage/plan enforcement and cost export.
  
  Acceptance
  - Usage counters can be queried/exported; plan enforcement blocks overage where configured.

## C. Tenant Manager requirements

### Access and team

- WHEN authenticating to the Dashboard — THE SYSTEM SHALL allow tenant managers to sign in using OIDC and see only their tenant context.
  
  Acceptance
  - UI reflects tenant id; cross-tenant data is not visible.

- WHEN managing team members — THE SYSTEM SHALL support creating roles, assigning permissions/scopes, and optional SSO (if allowed by admin).
  
  Acceptance
  - Users gain/lose access immediately according to role changes.

### API keys and webhooks

- WHEN creating API keys — THE SYSTEM SHALL issue scoped keys with optional expiry and rotation.
  
  Acceptance
  - Keys show last used; expired keys cannot be used; rotation preserves continuity.

- WHEN configuring webhooks — THE SYSTEM SHALL support tenant-scoped endpoints with secret signing and delivery retries.
  
  Acceptance
  - Failed deliveries retry with backoff; signatures validate.

### Database features

- WHEN managing collections/schemas — THE SYSTEM SHALL allow defining vector dimensions/metric and per-collection limits.
  
  Acceptance
  - Vector upsert/search requires `collection_id` and is routed to a single shard when possible.

- WHEN querying across collections — THE SYSTEM SHALL support two-step ANN (per-shard top-K gather + re-rank) when true cross-collection search is requested.
  
  Acceptance
  - Cross-collection searches return top-K within bounded latency without cross-shard ORDER BY merges.

- WHEN using REST DB APIs — THE SYSTEM SHALL provide CRUD with pagination, filtering, sorting, ETags for concurrency, and input validation.
  
  Acceptance
  - List endpoints return continuation tokens; 412 is returned on ETag mismatches; invalid inputs produce problem+json.

### Storage features

- WHEN managing buckets — THE SYSTEM SHALL support bucket creation, per-bucket quotas, lifecycle/retention, and object metadata/tags.
  
  Acceptance
  - Uploads are blocked when quotas are exceeded; lifecycle transitions occur according to policy.

- WHEN uploading/downloading objects — THE SYSTEM SHALL provide presigned URLs and multipart upload compatible with S3 SDKs.
  
  Acceptance
  - Presigned URLs honor expiry; multipart completes for large files; Content-Type validation applies; transformed variants cache per tenant and source ETag when using image transforms.

### Tenant observability and limits

- WHEN viewing tenant telemetry — THE SYSTEM SHALL show metrics/logs/traces scoped to the tenant.
  
  Acceptance
  - Only data with matching `tid` is visible; export is disabled unless policy allows.

- WHEN enforcing rate limits and cache policies — THE SYSTEM SHALL allow per-tenant overrides within admin-defined bounds.
  
  Acceptance
  - Changes affect only the tenant's routes and stay within guardrails.

## D. Service contracts

### Identity

- WHEN issuing tokens — THE SYSTEM SHALL support Authorization Code (PKCE), Client Credentials, Device Code, and Refresh Tokens, including `tid`, roles, scopes claims, and optional plan/quotas.
  
  Acceptance
  - OpenIddict discovery document published; Dashboard client can sign in; service-to-service calls succeed with client credentials; quotas available to enforcers.

- WHEN impersonation is enabled — THE SYSTEM SHALL provide APIs/UI to start/end impersonation with audit and limited scopes.
  
  Acceptance
  - Impersonation tokens are clearly marked and have reduced lifetime.

### Gateway

- WHEN resolving tenants — THE SYSTEM SHALL extract from subdomain or /t/{tenant} path (dev) and forward as `X-Tansu-Tenant`.
  
  Acceptance
  - Requests on {tenant}.example.local and /t/{tenant} resolve identical tid.

- WHEN enforcing safety controls — THE SYSTEM SHALL apply rate limits, request size limits, and per-route auth policies.
  
  Acceptance
  - Excess requests and oversize bodies result in appropriate 4xx responses with problem+json.

- WHEN testing response caching — THE SYSTEM SHALL provide a response cache policy tester and synthetic monitors per route with alerts.
  
  Acceptance
  - Test tool computes vary keys; synthetic checks alert on failures/latency regressions.

### Database API

- WHEN called with valid token and tid — THE SYSTEM SHALL perform operations against the tenant's database using EF Core and Npgsql and publish outbox events with idempotency keys for integrations.
  
  Acceptance
  - Cross-tenant access is blocked; migrations run per tenant deterministically; duplicate deliveries do not cause side effects.

### Storage API

- WHEN called with valid token and tid — THE SYSTEM SHALL operate on tenant-scoped buckets/objects with S3-compatible semantics.
  
  Acceptance
  - ETags preserved; conditional requests honored; soft delete optional; Brotli preserves weak ETags and sets Content-Encoding.

### Dashboard (Blazor Server)

- WHEN rendering UI — THE SYSTEM SHALL use Server render mode and support SignalR scale-out via Redis.
  
  Acceptance
  - Multiple instances share circuits via backplane; reconnects maintain state where possible.

- WHEN running long operations — THE SYSTEM SHALL execute as background jobs and display progress to users.
  
  Acceptance
  - UI shows job state and completion events; DbContext usage remains short?lived.

### SDK (NuGet)

- WHEN a .NET app calls AddTansuCloud(...) — THE SYSTEM SHALL register typed clients with auth handlers, retries, and distributed tracing.
  
  Acceptance
  - Typed clients are generated from OpenAPI; token acquisition/refresh is automatic; tenant selection is configurable.

## E. Experience enhancements and governance (backlog/optional)

### Governance and policy center

- WHEN managing platform policies — THE SYSTEM SHALL offer a Policy Center for CORS, IP allow/deny, rate limits, and export/audit policies.
  
  Acceptance
  - Policies apply per route/tenant/environment; changes audited and safely rolled out.

- WHEN scanning configurations — THE SYSTEM SHALL provide a secret scanner for configs and flag risky patterns.
  
  Acceptance
  - Scanner reports redacted findings with remediation tips; CI gate available.

- WHEN setting data residency — THE SYSTEM SHALL support data residency flags per tenant and enforce placement/backup locations accordingly.
  
  Acceptance
  - Tenant DB/storage targets match residency policy; violations are prevented or require explicit override with audit.

### Feature flags and chaos

- WHEN rolling out new features — THE SYSTEM SHALL support global and per-tenant feature flags with gradual rollout and targeting.
  
  Acceptance
  - Flags can target % of users/tenants; changes are tracked; rollout can be paused/rolled back.

- WHEN testing resilience — THE SYSTEM SHALL support chaos toggles in preview environments.
  
  Acceptance
  - Fault injection is scoped and disabled by default; impacts are observable and reversible.

### Identity & access UX

- WHEN delegating admin — THE SYSTEM SHALL allow delegated admin roles within a tenant with bounded scopes.
  
  Acceptance
  - Delegates can administer users/keys within scope; actions audited.

- WHEN managing API keys — THE SYSTEM SHALL send rotation reminders for expiring keys with easy rotate-now flow.
  
  Acceptance
  - Notifications are delivered; rotated keys overlap validity to avoid downtime.

### Gateway tools

- WHEN validating cache policy — THE SYSTEM SHALL compute vary keys and visualize OutputCache policy effects.
  
  Acceptance
  - Tool displays derived cache key components and TTL.

- WHEN monitoring routes — THE SYSTEM SHALL provide synthetic monitors per route with alerting thresholds.
  
  Acceptance
  - Alerts fire on latency/error thresholds and include tenant context.

### Performance & reliability

- WHEN restoring data — THE SYSTEM SHALL support time-travel restore per tenant to a prior point within retention policy.
  
  Acceptance
  - Restore creates a new tenant snapshot with clear lineage; downtime is minimized.

- WHEN tuning cache — THE SYSTEM SHALL provide cache heatmaps and TTL suggestions.
  
  Acceptance
  - Dashboard shows hit/miss distribution; suggests TTL changes with confidence.

- WHEN managing config changes — THE SYSTEM SHALL support canary checks and quick rollback for configuration changes.
  
  Acceptance
  - Changes are rolled out to a fraction of traffic; rollback is one click with audit trail.

- WHEN analyzing performance — THE SYSTEM SHALL provide slow query insights and vector index tuning hints.
  
  Acceptance
  - Recommendations list problematic queries and index actions with impact estimates.

### Observability UX

- WHEN inspecting logs — THE SYSTEM SHALL offer live log tail with structured filters and safe redaction.
  
  Acceptance
  - Real-time stream supports filter by `tid`/user/route; PII redaction enforced.

- WHEN diagnosing slowness — THE SYSTEM SHALL provide a "Why slow?" trace waterfall with downstream calls.
  
  Acceptance
  - Waterfall highlights spans with anomalies and links to logs/metrics.

- WHEN debugging errors — THE SYSTEM SHALL show error drill-down with sanitized payload samples.
  
  Acceptance
  - Error groups include sample context with secrets/PII removed.

- WHEN tracking cost/usage — THE SYSTEM SHALL display per-tenant cost/usage explorer (storage GB, egress, DB QPS, vector queries, cache hit rate).
  
  Acceptance
  - Explorer charts trends; exports CSV/JSON within policy.

### Storage UX

- WHEN using media transforms — THE SYSTEM SHALL provide transform presets (thumbnail/hero/avatar) with signed URLs and CDN hints.
  
  Acceptance
  - Presets validate max dimensions/concurrency; cache variants per tenant/source ETag.

- WHEN planning retention — THE SYSTEM SHALL provide a retention policy simulator.
  
  Acceptance
  - Simulator shows projected deletions and storage changes before applying.

### DevEx & automation

- WHEN creating blueprints — THE SYSTEM SHALL support templates that pre-provision DB/storage/policies.
  
  Acceptance
  - Blueprint can instantiate tenants with predefined resources and limits.

- WHEN creating preview environments — THE SYSTEM SHALL support ephemeral tenant clones with TTL and masked data.
  
  Acceptance
  - Clone contains non-sensitive data; auto-expires; isolation maintained.

- WHEN using CLI/recipes — THE SYSTEM SHALL provide a CLI and CI/CD recipes for common operations.
  
  Acceptance
  - CLI commands automate tenant ops and observability queries; recipes integrate with popular CI tools.

- WHEN managing as code — THE SYSTEM SHALL support export/import tenant as code (YAML).
  
  Acceptance
  - YAML round-trips with validation; imports are idempotent with drift detection.

### Vector & search

- WHEN experimenting with embeddings — THE SYSTEM SHALL support A/B testing of models/dimensions.
  
  Acceptance
  - Results compare recall/latency/cost per model; rollback path documented.

- WHEN monitoring vector collections — THE SYSTEM SHALL display collection health (index freshness, HNSW advisor, distribution key checks).
  
  Acceptance
  - Health view flags stale indexes, suboptimal distribution, and suggests maintenance.
