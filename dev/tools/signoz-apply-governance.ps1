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

# --- Apply hooks (dev-safe) ---

function Invoke-ClickHouseQuery {
    param(
        [Parameter(Mandatory=$true)][string]$Sql
    )
    # Use HTTP interface; prefer simple URL-encoded query for portability
    $uri = "$ClickHouseHttp/?query=$([uri]::EscapeDataString($Sql))"
    return Invoke-WebRequest -Uri $uri -Method Post -ContentType 'text/plain; charset=utf-8' -TimeoutSec 30 -ErrorAction Stop
} # End of Function Invoke-ClickHouseQuery

function Test-CHTableExists {
    param([string]$Db,[string]$Table)
    $sql = @"
SELECT 1
FROM system.tables
WHERE database = '$Db' AND name = '$Table'
LIMIT 1
"@
    try {
        $resp = Invoke-ClickHouseQuery -Sql $sql
        return ($resp.Content.Trim() -match '^1')
    } catch { return $false }
} # End of Function Test-CHTableExists

function Test-CHColumnExists {
    param([string]$Db,[string]$Table,[string]$Column)
    $sql = @"
SELECT 1
FROM system.columns
WHERE database = '$Db' AND table = '$Table' AND name = '$Column'
LIMIT 1
"@
    try {
        $resp = Invoke-ClickHouseQuery -Sql $sql
        return ($resp.Content.Trim() -match '^1')
    } catch { return $false }
} # End of Function Test-CHColumnExists

function Try-SetTTL {
    param([string]$Db,[string]$Table,[string]$TtlExpr,[switch]$WithDropParts)
    if (-not (Test-CHTableExists -Db $Db -Table $Table)) {
        Write-Host "[TTL] SKIP: Table $Db.$Table not found"
        return $false
    }
    $settings = if ($WithDropParts) { ' SETTINGS ttl_only_drop_parts = 1' } else { '' }
    $sql = "ALTER TABLE $Db.$Table ON CLUSTER cluster MODIFY TTL $TtlExpr$settings"
    try {
        Invoke-ClickHouseQuery -Sql $sql | Out-Null
        Write-Host "[TTL] OK: $Db.$Table ← $TtlExpr"
        return $true
    } catch {
        Write-Host "[TTL] FAIL: $Db.$Table ← $TtlExpr :: $($_.Exception.Message)"
        return $false
    }
} # End of Function Try-SetTTL

# Parse values with safe fallbacks for dev
$tracesDays = [int]($retTraces ?? 7)
$logsDays = [int]($retLogs ?? 7)
$metricsDays = [int]($retMetrics ?? 14)

Write-Host "--- Applying ClickHouse TTL retention (dev-safe) ---"

# Traces retention
# 1) Primary trace index (best-effort; only if 'timestamp' column exists)
if (Test-CHColumnExists -Db 'signoz_traces' -Table 'signoz_index_v3' -Column 'timestamp') {
    [void](Try-SetTTL -Db 'signoz_traces' -Table 'signoz_index_v3' -TtlExpr "toDateTime(timestamp) + toIntervalDay($tracesDays)" -WithDropParts)
} else {
    Write-Host "[TTL] SKIP: signoz_traces.signoz_index_v3 lacks 'timestamp' column or table missing"
}

# 2) Error index v2 (has 'timestamp' column per patches)
[void](Try-SetTTL -Db 'signoz_traces' -Table 'signoz_error_index_v2' -TtlExpr "toDateTime(timestamp) + toIntervalDay($tracesDays)" -WithDropParts)

# 3) Span attributes keys (TTL on timestamp)
[void](Try-SetTTL -Db 'signoz_traces' -Table 'span_attributes_keys' -TtlExpr "`"timestamp`" + toIntervalDay($tracesDays)")

# Logs retention (tag attributes table; main logs table varies by SigNoz version, so we change only known safe table)
[void](Try-SetTTL -Db 'signoz_logs' -Table 'tag_attributes_v2' -TtlExpr "toDateTime(unix_milli / 1000) + toIntervalDay($logsDays)")

# Metrics retention: no stable ClickHouse tables in this repo context → print planned value only
Write-Host "[TTL] INFO: Metrics retention planned = $metricsDays days (no metrics CH tables altered in this script)"

# Alert SLOs seeding (placeholder)
Write-Host "--- Alert SLOs (placeholder) ---"
if ($alerts -and $alerts.Count -gt 0) {
    foreach ($a in $alerts) {
        Write-Host ("Would ensure alert: id={0}, service={1}, kind={2}, window={3}m, threshold={4} {5}" -f $a.id, $a.service, $a.kind, $a.windowMinutes, $a.threshold, $a.comparison)
    }
    Write-Host "Note: Implement SigNoz alert rule creation via API in a future iteration."
} else {
    Write-Host "No alert rules specified."
}

# Sampling ratio handling: document only (collector/client side concern)
Write-Host "--- Sampler ratio ---"
Write-Host "Trace head sampling ratio requested = $ratio (configure via collector or service env; not changed here)"

Write-Host "Apply completed."
exit 0
