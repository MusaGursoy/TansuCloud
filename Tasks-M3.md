# Phase M3: Dashboard + SDK

[Back to index](./Tasks.md)

<!-- markdownlint-disable MD029 MD033 -->

## Checklist

- [ ] [17) Dashboard admin surfaces](#task-17-dashboard-admin-surfaces)
- [ ] [18) Dashboard tenant manager surfaces](#task-18-dashboard-tenant-manager-surfaces)
- [ ] [19) Observability pages](#task-19-observability-pages)
- [ ] [20) Background jobs UX](#task-20-background-jobs-ux)
- [ ] [21) .NET SDK (NuGet) alpha](#task-21-dotnet-sdk-alpha)
- [ ] [22) CLI tool (alpha)](#task-22-cli-tool-alpha)

---

## Tasks

<a id="task-17-dashboard-admin-surfaces"></a>

### Task 17: Dashboard admin surfaces

- Outcome: Instance admin pages: domains/TLS, YARP routes, output cache, rate limits, identity policies, DB/storage defaults, observability (retention/alerts/sampling/PII redaction), Policy Center (CORS/IP allow/deny).
- Dependencies: 3,6,9,13,16

<a id="task-18-dashboard-tenant-manager-surfaces"></a>

### Task 18: Dashboard tenant manager surfaces

- Outcome: Tenant pages: profile/domains, API keys, users/roles, buckets/lifecycle/quotas, DB collections/vector settings, webhooks, per-tenant rate/cache policies; transform presets; retention simulator.
- Dependencies: 11,13,15,14

<a id="task-19-observability-pages"></a>

### Task 19: Observability pages

- Outcome: Metrics/logs/traces for admin and tenant, scoped by tid; no direct backend exposure; live updates via SignalR; live log tail; "Why slow?" waterfall; error drill-down.
- Dependencies: 8

<a id="task-20-background-jobs-ux"></a>

### Task 20: Background jobs UX

- Outcome: Long-running operations surface progress notifications; job history with audit links; canary checks and rollback UI.
- Dependencies: 17-19

<a id="task-21-dotnet-sdk-alpha"></a>

### Task 21: .NET SDK (NuGet) alpha

- Outcome: AddTansuCloud(...) DI, typed clients from OpenAPI, auth handlers, retries (Polly), OTEL; token acquisition/refresh; tenant selection; cancellation.
- Dependencies: 11,13

<a id="task-22-cli-tool-alpha"></a>

### Task 22: CLI tool (alpha)

- Outcome: CLI for tenant ops (provision/clone/export/import YAML), observability queries, blueprint apply, synthetic monitor manage.
- Dependencies: 21,17,18

---

### Checklist item template

- [ ] <Task number>) <Task title>
  - Owner:
  - Status: Not Started | In Progress | Blocked | Done
  - Start:  YYYY-MM-DD   Due: YYYY-MM-DD
  - Notes:

<!-- markdownlint-enable MD029 MD033 -->
