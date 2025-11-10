$ErrorActionPreference = 'Stop'

Write-Host "=== Testing SigNoz API Response Structure ===" -ForegroundColor Cyan
Write-Host ""

# Time range
$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$start24h = $now - (24 * 3600 * 1000)

Write-Host "Time range: $start24h to $now"
Write-Host ""

# Let's check what the Dashboard is actually sending by looking at its logs with verbose output
Write-Host "Checking Dashboard logs for actual API calls..." -ForegroundColor Yellow
Write-Host ""

# Generate a search to trigger logging
Write-Host "Please perform a search in the Dashboard UI now, then press Enter..."
Read-Host

Write-Host ""
Write-Host "=== Recent Dashboard Logs ===" -ForegroundColor Green
docker logs tansu-dashboard --tail 100 2>&1 | Select-String -Pattern "query_range|Response|Building|Searching" | Select-Object -Last 20

Write-Host ""
Write-Host "=== Checking SigNoz container logs ===" -ForegroundColor Green
docker logs signoz --tail 50 2>&1 | Select-String -Pattern "query_range|POST|/api/v5" | Select-Object -Last 10
