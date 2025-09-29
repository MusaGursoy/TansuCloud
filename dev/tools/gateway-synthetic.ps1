param(
    [string]$BaseUrl,
    [string]$Tenant = "e2e",
    [int]$Concurrency = 8,
    [int]$RequestsPerRoute = 10
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null
$urls = Resolve-TansuBaseUrls

if (-not $PSBoundParameters.ContainsKey('BaseUrl') -or [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = $urls.PublicBaseUrl
}

Write-Host "Gateway synthetic monitors" -ForegroundColor Cyan
Write-Host "  BaseUrl=$BaseUrl Tenant=$Tenant Concurrency=$Concurrency RequestsPerRoute=$RequestsPerRoute"

function Invoke-Probe($Name, $Url, $Headers) {
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resp = Invoke-WebRequest -Uri $Url -Headers $Headers -Method GET -UseBasicParsing -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
        $sw.Stop()
        [PSCustomObject]@{
            route = $Name
            url = $Url
            status = $resp.StatusCode
            ms = [int]$sw.Elapsed.TotalMilliseconds
        }
    }
    catch {
        $sw.Stop()
        $status = if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { [int]$_.Exception.Response.StatusCode } else { 0 }
        [PSCustomObject]@{
            route = $Name
            url = $Url
            status = $status
            ms = [int]$sw.Elapsed.TotalMilliseconds
            error = $_.Exception.Message
        }
    }
}

$headers = @{ 'X-Tansu-Tenant' = $Tenant }

$routes = @(
    @{ name = 'gateway-root'; url = "$BaseUrl/" },
    @{ name = 'identity-discovery'; url = "$BaseUrl/identity/.well-known/openid-configuration" },
    @{ name = 'dashboard-root'; url = "$BaseUrl/dashboard" },
    @{ name = 'db-health'; url = "$BaseUrl/db/health/ready" },
    @{ name = 'storage-health'; url = "$BaseUrl/storage/health/ready" },
    @{ name = 'ratelimit-ping'; url = "$BaseUrl/ratelimit/ping" }
)

$results = New-Object System.Collections.Concurrent.ConcurrentBag[object]

foreach ($r in $routes) {
    Write-Host "Probing $($r.name) ..." -ForegroundColor Yellow
    1..$RequestsPerRoute | ForEach-Object -Parallel {
        param($route, $headers, $results)
        $res = Invoke-Probe -Name $route.name -Url $route.url -Headers $headers
        $results.Add($res)
    } -ThrottleLimit $Concurrency -ArgumentList $r, $headers, $results
}

# Summaries
$grouped = $results | Group-Object route | ForEach-Object {
    $ok = $_.Group | Where-Object { $_.status -ge 200 -and $_.status -lt 400 }
    $errors = $_.Group | Where-Object { $_.status -eq 0 -or $_.status -ge 400 }
    $lat = ($ok | Measure-Object ms -Average -Maximum -Minimum)
    [PSCustomObject]@{
        route = $_.Name
        count = $_.Count
        success = $ok.Count
        error = $errors.Count
        p50 = [int]($ok.ms | Sort-Object | Select-Object -Index ([Math]::Max(0,[Math]::Floor($ok.Count*0.5)-1)))
        p95 = [int]($ok.ms | Sort-Object | Select-Object -Index ([Math]::Max(0,[Math]::Floor($ok.Count*0.95)-1)))
        min = [int]$lat.Minimum
        avg = [int]$lat.Average
        max = [int]$lat.Maximum
    }
}

$grouped | Sort-Object route | Format-Table -AutoSize

# Check for clear rate-limit signal on ping
$rl = $results | Where-Object { $_.route -eq 'ratelimit-ping' -and $_.status -eq 429 } | Select-Object -First 1
if ($rl) {
    Write-Host "Rate limit 429 observed on /ratelimit/ping as expected." -ForegroundColor Green
} else {
    Write-Host "No 429 observed on /ratelimit/ping. Increase RequestsPerRoute/Concurrency to trigger limiter." -ForegroundColor DarkYellow
}
