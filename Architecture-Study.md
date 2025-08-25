# TansuCloud Proposed Architecture

## Purpose

- Provide a modular, self‑hosted backend platform where adopters can pick only the services they need.
- Support single-tenant and multi-tenant deployments with strong isolation guarantees.
- Ship first-class .NET dev experience (SDK, DI, typed clients) targeting .NET 9.

## Service catalog (projects in solution)

- **TansuCloud.Gateway**: ASP.NET Core Empty with YARP reverse proxy
  - Responsibilities: single ingress, TLS termination, routing, rate limiting, auth enforcement, tenant resolution, WebSocket/SignalR pass-through.
- **TansuCloud.Identity**: ASP.NET Core Web App (Razor Pages)
  - OpenIddict + ASP.NET Core Identity + EF Core (PostgreSQL)
  - Acts as OAuth2/OIDC provider issuing JWTs with tenant context and roles/scopes.
- **TansuCloud.Database**: ASP.NET Core Web API (Controllers)
  - PostgreSQL with Citus (enabled in single-node mode by default) + pgvector; PgCat as connection pooler.
  - EF Core + Npgsql. Tables declared as distributed/reference where appropriate to ease later multi-node scale-out.
  - CRUD + query APIs, multi-tenant data isolation, vector search endpoints.
- **TansuCloud.Storage**: ASP.NET Core Web API (Controllers)
  - In-house S3-compatible operations (buckets/prefixes, presigned URLs, multipart upload), quotas, lifecycle.
- **TansuCloud.Dashboard**: Blazor Web App (.NET 9)
  - Interactive render mode: Server; interactivity location: Global.
  - Admin and tenant management UI, configuration, health/metrics, audit views.
  - Single observability UI: surfacing metrics/logs/traces; Grafana/tempo/jaeger/elk not exposed to admins/tenants.

## Multi-tenancy model

- **Tenant identification**
  - Preferred: subdomain per tenant (e.g., {tenant}.example.com). Alternate: path prefix /t/{tenant} for development.
  - Gateway extracts tenant id and forwards it via X-Tansu-Tenant and tid claim in tokens.
- **Data isolation**
  - Default: database-per-tenant for strict isolation. Each tenant database enables the Citus and pgvector extensions in single-node mode by default, so later attaching workers yields intra-tenant sharding with no schema changes. Optional RLS within each tenant DB for fine-grained app authorization.
- **Provisioning lifecycle**
  - Create tenant record -> provision database (from template with Citus/pgvector enabled) -> run migrations -> seed default roles/config -> issue admin invite -> emit audit/event.
- **Cross-service propagation**
  - Tokens include tid, roles, scopes. Services require tid and enforce per-tenant scope checks.

## Gateway (YARP)

- **Routes (dev-friendly defaults)**
  - /dashboard -> TansuCloud.Dashboard
  - /identity -> TansuCloud.Identity (OIDC endpoints under /identity/connect/*)
  - /db -> TansuCloud.Database
  - /storage -> TansuCloud.Storage
- **Subdomain model**
  - {tenant}.example.local forwards to app endpoints with X-Tansu-Tenant header and preserves Authorization.
- **Requirements**
  - WebSockets/SignalR enabled for Blazor Server.
  - Optional sticky sessions when horizontally scaling Blazor Server.
  - Output caching for safe GETs using ASP.NET Core OutputCache at the gateway. Vary-by: tenant, path, query, selected headers. Bypass when Authorization header is present unless route is explicitly marked public. Respect/forward ETag and Last-Modified for conditional requests. Per-route policy configuration.
  - Rate limiting, request body size limits, per-route auth policies, and response caching where safe.

## Identity (OpenIddict + ASP.NET Identity)

- **Flows**: Authorization Code (PKCE), Client Credentials (service-to-service), Device Code (CLI), Refresh Tokens.
- **Claims**: sub, tid, roles, name, email, permissions; optional plan/quotas.
- **Scopes**: db.read, db.write, storage.read, storage.write, admin.*
- **Features**: MFA, passwordless (optional), key rotation, external IdP (OIDC) per-tenant, audit logs, admin impersonation.

## Database service

- **Storage layout**
  - Database-per-tenant. Each tenant has a dedicated PostgreSQL database with Citus and pgvector enabled. Use a template database to speed provisioning and ensure extensions are present.
  - Declare main tables as Citus distributed tables from day one using create_distributed_table; choose distribution keys to preserve single-shard routing (e.g., collection_id for vector collections). Use reference tables for small, global dictionaries.
  - For vector data, maintain HNSW indexes per shard; sequences remain per-tenant.
  - Optional RLS within a tenant DB for fine-grained authorization.
- **Vector on Citus (guidelines)**
  - Require a collection/context filter (e.g., collection_id) on vector queries so Citus can route to a single shard; this preserves ANN performance and avoids cross-shard merges for ORDER BY embedding <-> ... LIMIT k.
  - If true cross-collection ANN is needed, perform two-step search (per-shard top-K with gather + re-rank) or constrain distribution to keep hot collections on one shard.
  - Keep hybrid search (BM25 + vector) colocated by using the same distribution key in text and vector tables.
- **Access layer**
  - EF Core compiled models, Npgsql type mappings, deterministic migrations per tenant database (including create_distributed_table and create_reference_table).
- **APIs**
  - Versioned REST endpoints, pagination/filters, validation, OpenAPI.
  - Vector endpoints support upsert/search within a collection and require a collection_id.
- **Reliability**
  - Outbox + background worker for reliable integration events; retries with idempotency keys.
  - When scaling to multi-node, use Citus rebalance operations to move shards; no API change required.

## Storage service

- **S3-compatible operations**
  - Buckets/prefixes per tenant, presigned upload/download, multipart, object metadata/tags, soft delete, lifecycle rules.
- **Optional features**
  - On-the-fly Brotli compression for compressible content-types (e.g., text/*, application/json). Honors Accept-Encoding; sets Content-Encoding and preserves weak ETags. Disable for already-compressed media.
  - Programmatic image transformations (resize/fit/crop/format/quality). Secured with signed URLs; limits on max dimensions and concurrency; caches transformed variants per tenant and source ETag.
- **Security**
  - Server-side encryption support, content-type validation, optional AV scan hook, size limits, quotas, audit.

## Dashboard (Blazor Server)

- **Scale-out**: Redis backplane for SignalR when running multiple instances.
- **UX**: tenant/user/role management, API keys, quotas, service configuration, health, metrics, logs, audit trails.
- **Observability UI**: built-in charts/tables for metrics, logs, traces with tenant scoping (tid). Queries proxied from the dashboard to observability backends; no direct Grafana access for admins/tenants.
- **Configuration management**
  - Instance admin
    - Domains and TLS certificates; YARP routes, output-cache policies, and rate limits.
    - Identity policies: password/MFA, external IdP registrations, token lifetimes, JWKS rotation.
    - Database: template DB settings, extensions, distribution keys defaults, shard rebalancing, backup/restore.
    - Storage: global lifecycle rules, encryption defaults, image transform policies, Brotli policy, virus scan settings.
    - Caching: Redis endpoints, HybridCache defaults (TTLs, size), cache invalidation settings.
    - Observability: retention, alert rules, sampling, PII redaction.
    - Plans/quotas, billing hooks, global feature flags, audit/export policies.
  - Tenant manager
    - Tenant profile and custom domains; API keys and per-scope permissions.
    - Storage buckets, lifecycle/retention, per-bucket quotas; image transform presets.
    - Database collections/schemas setup, vector settings (dimensions, metric), per-collection limits.
    - Webhooks, rate limits for their APIs, cache policies for their endpoints.
    - Team management: users/roles within the tenant, SSO settings if allowed by admin.
- **Patterns**: short-lived DbContext usage, long-running ops via background jobs with progress notifications.

## Observability and operations

- OpenTelemetry traces/metrics/logs attached in every service (Gateway, Identity, Database, Storage, Dashboard).
- Use a shared OpenTelemetry Collector as infrastructure (container/sidecar/daemonset). Services export OTLP to the collector; the collector fans out to storage/backends (e.g., Prometheus for metrics, Tempo/Jaeger for traces, Seq/ELK for logs). These backends are internal-only.
- Dashboard provides the only UI for instance admins and tenants to view telemetry; it queries the backends via HTTP/OTLP and enforces RBAC and tenant filtering using tid attributes.
- Exporters: Prometheus (via collector), OTLP to tracing/log backends; console exporters for local development.
- Propagation: W3C distributed tracing headers preserved by the gateway; include tenant context (tid) as span/metric/log attributes via enrichers/baggage.
- Health endpoints: liveness/readiness with dependency checks; startup probes.

## Caching

- Use .NET HybridCache in all services to combine in-process memory and a distributed cache for scale-out.
- Distributed backing store: Redis (shared instance already used for Blazor Server backplane). Local dev can run memory-only.
- Keying: always prefix with tenant (tid) and a resource namespace, e.g., t:{tid}:users:v1:{id}. Avoid cross-tenant leakage by construction.
- TTLs: short defaults (30s–15m) based on data volatility; vector search results typically 30–120s; configuration 5–15m.
- Stampede control: rely on HybridCache GetOrCreateAsync de-duplication; prefer soft-TTL + background refresh for hot keys.
- Invalidation: publish cache-bust messages from the outbox to Redis pub/sub; fall back to versioned keys per tenant when messaging is unavailable.
- What to cache
  - Database: tenant metadata, connection info, feature flags, small lookups, vector collection metadata (not PII).
  - Storage: object/bucket metadata lists (bounded), not object contents.
  - Identity: discovery/JWKS and policy documents; keep user/role lookups short-lived to respect revocation.
  - Gateway: response caching for safe GETs via OutputCache with ETags/Last-Modified.
- Dashboard: use IMemoryCache for ephemeral UI/circuit state; do not store secrets in cache.

## Experience enhancements (backlog)

- Dashboard UX
  - One‑click service Playgrounds (tenant‑scoped tokens) and guided wizards (onboarding, domains/TLS, buckets, vector collections).
  - Saved views and shareable dashboards; in‑app docs/snippets with curl/SDK examples.
- DevEx & automation
  - Blueprints/templates that pre‑provision DB/storage/policies; preview environments (ephemeral tenant clones with TTL, masked data).
  - CLI and CI/CD recipes generated from selected settings.
- Performance & reliability
  - Time‑travel restore per tenant; cache heatmaps and TTL suggestions; canary checks and rollback for config changes.
  - Slow query insights and vector index tuning hints.
- Observability in Dashboard
  - Live log tail with structured filters; “Why slow?” trace waterfall; error drill‑down with sanitized payload samples.
  - Cost/usage explorer per tenant (storage GB, egress, DB QPS, vector queries, cache hit rate).
- Storage & media
  - Transform presets (thumbnail/hero/avatar) with signed URLs and CDN hints; retention policy simulator.
- Identity & access
  - Delegated admin, expiring API keys with rotation reminders; impersonation with reason/time limit and auto‑audit.
- Security & governance
  - Policy center (CORS, IP allow/deny, rate limits); secret scanner for configs; data residency flags per tenant.
- Vector & search
  - Embedding A/B testing (models/dimensions) and collection health (index freshness, HNSW advisor, distribution key checks).
- Gateway tools
  - Response cache policy tester (vary keys, TTL); synthetic monitors per route with alerts.
- Extras
  - Export/import tenant as code (YAML); feature flags per tenant with gradual rollout; chaos toggles in preview envs.

## API design policies

- Versioning via URL segment (v1) or header; deprecation policy documented.
- Consistent error shape with problem+json; pagination (continuation tokens), filtering, sorting; ETags for conditional requests.
- OpenAPI for all services; generated SDK clients.

## Security

- Centralized authorization policies; services enforce scopes/roles per route.
- Observability backends (Prometheus/Tempo/Jaeger/Seq/ELK) are network-restricted and not exposed externally; only the Dashboard can access them.
- Secrets via environment/KeyVault-compatible abstraction; encryption in transit and at rest.
- Tenant isolation tests; rate limiting and basic WAF rules at gateway.

## SDK (NuGet)

- AddTansuCloud(...) DI registration; typed REST clients (generated), auth handlers, retries (Polly), distributed tracing.
- Handles token acquisition/refresh, tenant selection, and cancellation.

## Deployment

- Containers for each service; docker-compose for local dev with PostgreSQL (Citus single-node coordinator image), PgCat, Redis (used for Blazor backplane and HybridCache), OpenTelemetry Collector, optional telemetry backends (Prometheus/Tempo/Seq) not exposed publicly.
- Easy path to multi-node: add Citus workers, run master_add_node for each, and rebalance shards; per-tenant databases already have distributed metadata.
- Helm charts for Kubernetes later; backup/restore procedures for PostgreSQL and object storage.

## Initial milestones

- **M1**: Gateway + Identity
  - YARP with routes and tenant extraction; Identity issuing JWTs with tid and roles; dashboard login working.
- **M2**: Database + Storage (core)
  - Database-per-tenant with Citus (single-node) and pgvector; basic CRUD and vector endpoints; S3-compatible upload/download with presigned URLs.
- **M3**: Dashboard + SDK
  - Admin/tenant management, health and metrics; Observability pages (metrics/logs/traces) with tenant scoping; .NET SDK with typed clients and auth.

## Next steps

- Scaffold YARP config and identity server skeleton; define token claims and scopes.
- Establish tenant model and provisioning service; enable Citus/pgvector in template DB; define distribution keys and create_distributed_table calls in migrations.
- Add OpenTelemetry to all services and a shared collector in docker-compose; wire Dashboard to query telemetry backends; configure default resource attributes (service.name, service.version, deployment.environment, tid).
- Add HybridCache to all services; configure Redis in shared infra; define cache key conventions and invalidation events.
- Define OutputCache policies in the Gateway and per-route configuration (vary keys, TTLs, auth bypass rules).
- Define OpenAPI contracts and generate initial SDK clients.
