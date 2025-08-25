# Architecture: TansuCloud (Initial)

## Contents

- [Overview](#overview)
- [Component architecture](#component-architecture)
- [Governance & Policy Center](#governance--policy-center)
- [Multi-tenancy](#multi-tenancy)
- [Data and distribution](#data-and-distribution)
- [Caching](#caching)
- [Observability](#observability)
- [Security](#security)
- [API policies](#api-policies)
- [Deployment](#deployment)
- [Blazor and Razor specifics](#blazor-and-razor-specifics)
- [Initial milestones](#initial-milestones)

## Overview

- Modular, self-hosted platform with multi-tenant isolation and first-class .NET 9 experience.
- Services: Gateway (YARP), Identity (Razor Pages + OpenIddict), Database (Web API + EF Core on PostgreSQL with Citus/pgvector), Storage (Web API, S3-compatible), Dashboard (Blazor Server), SDK.
- Cross-cutting: Multi-tenancy, Security/AuthZ, Observability (OpenTelemetry), Caching (HybridCache + Redis), Provisioning, Output caching at gateway.

## Component architecture

- Gateway (TansuCloud.Gateway)
  - ASP.NET Core w/ YARP reverse proxy; routes /dashboard, /identity, /db, /storage.
  - Tenant resolution: subdomain or /t/{tenant}. Adds X-Tansu-Tenant; preserves Authorization and tracing headers.
  - OutputCache policies for safe GET (bypass when Authorization is present unless route is explicitly public); vary-by tenant/path/query/headers.
  - Per-route auth policies and rate limiting; request body size limits; WebSockets/SignalR; optional sticky sessions for Blazor Server; TLS termination.
  - Tools: Response cache policy tester (compute/visualize vary keys, TTL); synthetic route monitors with alert thresholds.

- Identity (TansuCloud.Identity)
  - ASP.NET Core Web App (Razor Pages); OpenIddict + ASP.NET Identity + EF Core (PostgreSQL).
  - Flows: Auth Code (PKCE), Client Credentials, Device Code, Refresh Tokens.
  - Claims: sub, tid, roles, name, email, permissions; scopes: db.read/write, storage.read/write, admin.*
  - JWKS rotation; MFA/passwordless; per-tenant external OIDC providers (if enabled); admin impersonation; delegated admin; audit logs for security events; API key rotation reminders.

- Database (TansuCloud.Database)
  - ASP.NET Core Web API (Controllers);
  - PostgreSQL with Citus (single-node by default) and pgvector; PgCat as connection pooler.
  - EF Core compiled models, deterministic migrations. Tables declared distributed/reference; HNSW indexes for vectors.
  - Vector guidelines: require collection filter for single-shard routing; support two-step cross-collection ANN (per-shard top-K gather + re-rank); colocate hybrid search by sharing distribution key.
  - Reliability: Outbox + background worker for integration events; retries with idempotency keys; later scale-out via Citus rebalance without API changes; time-travel restore per tenant.
  - Insights: slow query analysis, vector index tuning hints, collection health (freshness, distribution key checks).
  - APIs: versioned CRUD, query, vector upsert/search (require collection_id). ETags, pagination, filters, validation, OpenAPI.

- Storage (TansuCloud.Storage)
  - ASP.NET Core Web API (Controllers) with multi-tenant S3-like features: buckets/prefixes, presigned URLs, multipart.
  - Policies: quotas, lifecycle, server-side encryption; content-type validation; request size limits; optional AV scan hook.
  - Optional: Brotli compression for compressible types (honors Accept-Encoding; sets Content-Encoding; preserves weak ETags; disabled for already-compressed media); image transforms (resize/fit/crop/format/quality) via signed URLs with per-tenant caching keyed by source ETag and transform presets (thumbnail/hero/avatar); retention policy simulator.
  - Audit logs for administrative operations.

- Dashboard (TansuCloud.Dashboard)
  - Blazor Web App (.NET 9). Interactive render mode: Server; location: Global.
  - Features: admin + tenant management, configuration, health/metrics/logs/traces, audit views; configuration management for domains/TLS, routes, rate limits, cache, identity policies, DB/storage defaults, observability (retention/alerts/sampling/PII redaction), Policy Center (CORS, IP allow/deny).
  - Observability UX: live log tail with structured filters and redaction, "Why slow?" trace waterfall, error drill-down with sanitized samples, cost/usage explorer (storage GB, egress, DB QPS, vector queries, cache hit rate).
  - Performance & reliability UX: cache heatmaps and TTL suggestions; canary checks and rollback for config changes; time-travel restore per tenant; vector collection health dashboard with HNSW advisor.
  - DevEx UX: service Playgrounds (tenant-scoped tokens), wizards for onboarding/domains/buckets/vector collections; saved views; in-app docs/snippets.
  - Patterns: short-lived DbContext usage; long-running operations executed as background jobs with progress updates to UI.
  - Scale-out: uses Redis backplane for SignalR when scaled; IMemoryCache for ephemeral UI state.

- SDK (NuGet)
  - AddTansuCloud(...) registers typed REST clients (generated from OpenAPI), auth handlers, retries (Polly), distributed tracing (OTEL).
  - Handles token acquisition/refresh, tenant selection, and request cancellation propagation.
  - CLI: companion tool for tenant ops, observability queries, blueprint apply, and synthetic monitors.

## Governance & Policy Center

- Centralized configuration for CORS, IP allow/deny, rate limits, and export/audit policies.
- Secrets scanner integrated in Dashboard and CI; findings redacted with remediation.
- Data residency flags per tenant influence DB/storage placement and backup/restore targeting.
- Feature flags: global and per-tenant with gradual rollout and targeting; chaos toggles in preview envs.

## Multi-tenancy

- Tenant identification: preferred subdomain; dev alternative /t/{tenant}.
- Context propagation: X-Tansu-Tenant header and tid claim.
- Data isolation: database-per-tenant; optional RLS within tenant DB.
- Provisioning: create tenant -> provision DB from template (extensions enabled) -> run migrations -> seed roles/config -> invite admin -> emit audit/event.
- Blueprints: templates that pre-provision DB/storage/policies; preview environments via ephemeral tenant clones with TTL and masked data.
- Tenant as code: export/import YAML with validation, idempotency, and drift detection.

## Data and distribution

- Template DB enables Citus + pgvector; initial schema declares distributed tables with chosen distribution keys (e.g., collection_id) and reference tables for globals.
- Vector search: enforce collection filter for single-shard routing; cross-collection via two-step gather/re-rank; hybrid search colocated via same distribution key.

## Caching

- HybridCache in all services. Redis as distributed backing store and Blazor backplane.
- Keying format: t:{tid}:{resource}:v1:{id}. TTLs vary by volatility; stampede control via GetOrCreateAsync.
- Invalidation: Redis pub/sub from outbox; versioned keys fallback.
- Guidance: per-service cache targets (Identity discovery/JWKS; DB tenant metadata/lookups; Storage object/bucket metadata lists; Gateway safe GET responses; Dashboard ephemeral UI only).

## Observability

- OpenTelemetry across services to a shared OTEL Collector; exporters (Prometheus via collector, OTLP to tracing/log backends); backends (Prometheus/Tempo/Seq/ELK) internal only.
- Dashboard queries backends via HTTP/OTLP; enforces RBAC and tid filters.
- Propagate W3C tracing headers; enrich with tid and user.
- Health endpoints: liveness/readiness with dependency checks; startup probes.

## Security

- Centralized authorization policies; scopes/roles enforced per route and at gateway where configured.
- Secrets via environment/KeyVault-compatible providers; TLS termination at gateway.
- Rate limiting and basic WAF-like rules at gateway.
- Tenant isolation tests as part of quality gates; secret scanner in CI.

## API policies

- Versioning: URL segment (v1) or header; consistent problem+json errors; pagination with continuation tokens; ETags for conditional requests; OpenAPI for all services.

## Deployment

- Containers per service; docker-compose for local dev with PostgreSQL (Citus single-node), PgCat, Redis, OTEL Collector, optional telemetry backends.
- Scale-out path: add Citus workers, master_add_node, rebalance; services unchanged.
- Roadmap: Helm charts for Kubernetes; backup/restore procedures for PostgreSQL and object storage.
- CI/CD recipes; CLI integration for tenant ops; synthetic monitors deployment.

## Blazor and Razor specifics

- Dashboard is Blazor Server: ensure WebSockets/SignalR via gateway; consider sticky sessions for multiple instances.
- Identity uses Razor Pages for OpenIddict management UI flows.

## Initial milestones

- M1: Gateway + Identity, dashboard login.
- M2: Database + Storage core APIs.
- M3: Dashboard management + SDK typed clients and observability pages.
- M4: Experience enhancements and governance (policy center, feature flags, gateway tools, insights, CLI, blueprints, tenant-as-code, cost/usage explorer).
