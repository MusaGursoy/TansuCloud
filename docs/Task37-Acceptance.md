# Task 37 – BAAS Product Telemetry Job Acceptance Bundle

_Date:_ 2025-10-02

This note packages the evidence used to validate Task 37. It should accompany the PR that marks the task complete.

## Test & Environment Evidence

| Check | Command / Source | Result |
| --- | --- | --- |
| Compose app stack | `docker compose --env-file ./.env up -d identity dashboard db storage gateway` | Pass – all services healthy within ~36s |
| Gateway/Dashboard/Identity/DB/Storage health | `dotnet test tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.HealthEndpointsE2E` | Pass (10/10) |
| Dashboard metrics smoke | `dotnet test tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.DashboardMetricsSmoke.Metrics_Page_Renders` | Pass (1/1) |

## Heartbeat Validation Artifacts

- Screenshot: `.playwright-mcp/dashboard-logs-heartbeat.png`
  - Captures Admin → Logs page with runtime toggle enabled and the “Send test report” action exercised.
- Log archive: `test-results/dashboard-heartbeat-log.txt`
  - Includes the expected `System.Net.Http.HttpRequestException: Name or service not known (telemetry.tansu.cloud:443)` stack trace demonstrating DNS failure in development while the reporter retry logic was engaged.

## Notes

- The failure is expected in local development because `telemetry.tansu.cloud` is not resolvable. The reporter retains the batch and backs off, matching the task’s requirements.
- `Guide-For-Admins-and-Tenants.md` already documents outbound payload scope, opt-out, env keys, and SigNoz posture; no further updates were required for this evidence pass.
