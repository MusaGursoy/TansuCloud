$ErrorActionPreference = 'Stop'

$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000000
$start24h = $now - (24L * 3600 * 1000000000)

Write-Host "Now (nano): $now"
Write-Host "Start 24h ago (nano): $start24h"
Write-Host "Start 24h ago (ms): $($start24h / 1000000)"
Write-Host "Now (ms): $($now / 1000000)"

# Log timestamps from database
$logMin = 1762202210183067100
$logMax = 1762209782838198000

Write-Host "`nLog timestamps from DB:"
Write-Host "Log min (nano): $logMin"
Write-Host "Log max (nano): $logMax"
Write-Host "Is log min > start? $($logMin -gt $start24h)"
Write-Host "Is log max < now? $($logMax -lt $now)"
