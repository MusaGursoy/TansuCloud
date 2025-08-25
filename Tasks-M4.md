# Phase M4: Experience enhancements and governance

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [23) Policy Center full](#task-23-policy-center-full)
- [ ] [24) Secrets scanner](#task-24-secrets-scanner)
- [ ] [25) Feature flags and chaos](#task-25-feature-flags-and-chaos)
- [ ] [26) Cost/usage explorer](#task-26-costusage-explorer)
- [ ] [27) Blueprints and preview environments](#task-27-blueprints-and-preview-environments)
- [ ] [28) Tenant as code (YAML)](#task-28-tenant-as-code)
- [ ] [29) Performance insights](#task-29-performance-insights)
- [ ] [30) Gateway tools](#task-30-gateway-tools)
- [ ] [31) Security and audit logging](#task-31-security-and-audit-logging)
- [ ] [32) Health endpoints + startup probes validation](#task-32-health-endpoints--startup-probes-validation)
- [ ] [33) CI build/test, containerization, compose](#task-33-ci-buildtest-containerization-compose)

---

## Tasks

<a id="task-23-policy-center-full"></a>

### Task 23: Policy Center full

- Outcome: Centralized CORS, IP allow/deny, rate limits, export/audit policies with staged rollout and audit.
- Dependencies: 17

<a id="task-24-secrets-scanner"></a>

### Task 24: Secrets scanner

- Outcome: Secret scanner integrated in Dashboard and CI with redacted reports and remediation.
- Dependencies: 24

<a id="task-25-feature-flags-and-chaos"></a>

### Task 25: Feature flags and chaos

- Outcome: Global/per-tenant flags with targeting and gradual rollout; chaos toggles in preview envs.
- Dependencies: 17

<a id="task-26-costusage-explorer"></a>

### Task 26: Cost/usage explorer

- Outcome: Per-tenant cost/usage charts (storage GB, egress, DB QPS, vector queries, cache hit rate); CSV/JSON export.
- Dependencies: 19

<a id="task-27-blueprints-and-preview-environments"></a>

### Task 27: Blueprints and preview environments

- Outcome: Blueprints to pre-provision resources/policies; ephemeral tenant clones with TTL and masked data.
- Dependencies: 9,17,22

<a id="task-28-tenant-as-code"></a>

### Task 28: Tenant as code (YAML)

- Outcome: Export/import with idempotency and drift detection; validations and dry-run.
- Dependencies: 22,27

<a id="task-29-performance-insights"></a>

### Task 29: Performance insights

- Outcome: Slow query analysis, vector index tuning hints, cache heatmaps and TTL suggestions.
- Dependencies: 11,15,19

<a id="task-30-gateway-tools"></a>

### Task 30: Gateway tools

- Outcome: Response cache policy tester UI; synthetic monitors with alerting thresholds per route/tenant.
- Dependencies: 16,19

<a id="task-31-security-and-audit-logging"></a>

### Task 31: Security and audit logging

- Outcome: Central audit trail for admin/tenant actions; impersonation logs; retention settings; export policies.
- Dependencies: 6,17,18

<a id="task-32-health-endpoints--startup-probes-validation"></a>

### Task 32: Health endpoints + startup probes validation

- Outcome: /health/live and /health/ready across services; docker-compose integration.
- Dependencies: 8

<a id="task-33-ci-buildtest-containerization-compose"></a>

### Task 33: CI build/test, containerization, compose

- Outcome: Build pipelines, unit/integration tests, docker images, docker-compose for local dev (PostgreSQL+Citus, PgCat, Redis, OTEL, optional telemetry backends).
- Dependencies: 1-30

---

### Checklist item template

- [ ] <Task number>) <Task title>
  - Owner:
  - Status: Not Started | In Progress | Blocked | Done
  - Start:  YYYY-MM-DD   Due: YYYY-MM-DD
  - Notes:

<!-- markdownlint-enable MD029 MD033 -->
