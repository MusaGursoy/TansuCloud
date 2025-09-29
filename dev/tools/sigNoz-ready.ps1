param(
    [string]$OtlpHost,
    [int]$OtlpPort,
    [string]$ClickHouseHttp
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null

if ([string]::IsNullOrWhiteSpace($OtlpHost)) { $OtlpHost = $env:OTLP_GRPC_HOST }
if (-not $OtlpPort) { $OtlpPort = [int]($env:OTLP_GRPC_PORT) }
if ([string]::IsNullOrWhiteSpace($ClickHouseHttp)) { $ClickHouseHttp = $env:CLICKHOUSE_HTTP }

if ([string]::IsNullOrWhiteSpace($OtlpHost)) { $OtlpHost = '127.0.0.1' }
if (-not $OtlpPort) { $OtlpPort = 4317 }
if ([string]::IsNullOrWhiteSpace($ClickHouseHttp)) { $ClickHouseHttp = 'http://127.0.0.1:8123' }

$otlpReady = $false
try {
    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect($OtlpHost, $OtlpPort, $null, $null)
    [void]$iar.AsyncWaitHandle.WaitOne(3000)
    if ($client.Connected) { $client.Close(); $otlpReady = $true } else { $client.Close() }
} catch { $otlpReady = $false }

$chReady = $false
try {
    $resp = Invoke-WebRequest -Uri $ClickHouseHttp -Method Head -TimeoutSec 3 -ErrorAction Stop
    if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 500) { $chReady = $true }
} catch { $chReady = $false }

if ($otlpReady -and $chReady) {
    Write-Host "SigNoz ready: YES (OTLP $($OtlpHost):$OtlpPort, ClickHouse $ClickHouseHttp)"
} else {
    Write-Host "SigNoz ready: NO (OTLP $($OtlpHost):$OtlpPort and/or ClickHouse $ClickHouseHttp not reachable)"
}
    exit 0 # soft gate: do not fail callers
