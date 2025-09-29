param(
    [string]$ClickHouseHttp,
    [string]$DefaultsPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null

if ([string]::IsNullOrWhiteSpace($ClickHouseHttp)) { $ClickHouseHttp = $env:CLICKHOUSE_HTTP }
if ([string]::IsNullOrWhiteSpace($ClickHouseHttp)) { $ClickHouseHttp = 'http://127.0.0.1:8123' } # Dev default, matches sigNoz-ready.ps1

if ([string]::IsNullOrWhiteSpace($DefaultsPath)) { $DefaultsPath = Join-Path (Split-Path $PSScriptRoot -Parent) '..' | Join-Path -ChildPath 'SigNoz/governance.defaults.json' }
$DefaultsPath = Resolve-Path $DefaultsPath

Write-Host "SigNoz governance: using ClickHouse HTTP = $ClickHouseHttp"
Write-Host "SigNoz governance: loading defaults from  = $DefaultsPath"

if (-not (Test-Path $DefaultsPath)) { throw "Defaults file not found: $DefaultsPath" }
$json = Get-Content $DefaultsPath -Raw | ConvertFrom-Json

# Simple readiness check (HEAD)
try {
    $resp = Invoke-WebRequest -Uri $ClickHouseHttp -Method Head -TimeoutSec 5 -ErrorAction Stop
    if ($resp.StatusCode -lt 200 -or $resp.StatusCode -ge 500) { throw "ClickHouse endpoint is not healthy: $($resp.StatusCode)" }
} catch {
    Write-Host "SigNoz governance: ClickHouse not reachable at $ClickHouseHttp. Exiting."
    exit 2
}

# NOTE: This script is currently a DRY-RUN translator showing intended actions.
# Applying actual retention and alert rules depends on SigNoz API/ClickHouse DDL specifics.
# We avoid destructive changes by default; use -Apply to enable once rules are finalized.

Write-Host "--- Governance defaults preview ---"
Write-Host (ConvertTo-Json $json -Depth 6)

# Preview actions
$retTraces = $json.retentionDays.traces
$retLogs = $json.retentionDays.logs
$retMetrics = $json.retentionDays.metrics
$ratio = $json.sampling.traceRatio
$alerts = $json.alertSLOs

Write-Host "Planned retention (days): traces=$retTraces, logs=$retLogs, metrics=$retMetrics"
Write-Host "Planned sampling: trace head ratio=$ratio"
Write-Host ("Planned alert SLOs: {0} rules" -f $alerts.Count)

if (-not $Apply) {
    Write-Host "Dry-run complete. Re-run with -Apply to enforce when implementation hooks are added."
    exit 0
}

# Placeholder for future implementation:
# - Adjust TTL settings for signoz_* tables (metrics/traces/logs) using ALTER TABLE ... TTL in ClickHouse
# - Seed/update alert rules via SigNoz APIs (when available)
# - Configure OTEL sampler ratio via environment or collector config (outside the scope of this script)

Write-Host "Apply mode is not yet implemented to avoid accidental destructive changes."
exit 3
