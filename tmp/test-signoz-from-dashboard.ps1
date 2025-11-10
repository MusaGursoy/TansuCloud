$ErrorActionPreference = 'Stop'

# Time range (milliseconds) - last 24 hours
$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$start24h = $now - (24 * 3600 * 1000)

Write-Host "=== Calling SigNoz API from Dashboard container ===" -ForegroundColor Cyan
Write-Host "Time range: $start24h to $now (24 hours)"
Write-Host ""

# Create the JSON payload
$json = @"
{
  "schemaVersion": "v1",
  "start": $start24h,
  "end": $now,
  "requestType": "raw",
  "compositeQuery": {
    "queries": [{
      "type": "builder_query",
      "spec": {
        "name": "A",
        "signal": "logs",
        "disabled": false,
        "aggregations": [],
        "filter": { "expression": "" },
        "limit": 100,
        "offset": 0,
        "having": { "expression": "" }
      }
    }]
  },
  "formatOptions": { "formatTableResultForUI": true, "fillGaps": false },
  "variables": {}
}
"@

Write-Host "Payload:"
Write-Host $json
Write-Host ""

# Save to temp file
$json | Out-File -FilePath c:\Users\gurso\Documents\NET\TansuCloud\tmp\signoz-payload.json -Encoding UTF8 -NoNewline

# Copy to Dashboard container
docker cp c:\Users\gurso\Documents\NET\TansuCloud\tmp\signoz-payload.json tansu-dashboard:/tmp/payload.json

# Call API from Dashboard container (it has HTTP client capabilities)
Write-Host "Calling API..." -ForegroundColor Yellow
$result = docker exec tansu-dashboard wget -q -O - --header="Content-Type: application/json" --post-file=/tmp/payload.json http://signoz:3301/api/v5/query_range

Write-Host ""
Write-Host "=== Response ===" -ForegroundColor Green
if ($result) {
    $result | ConvertFrom-Json | ConvertTo-Json -Depth 10 | Write-Host
} else {
    Write-Host "Empty response" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== ClickHouse Verification ===" -ForegroundColor Cyan
$chResult = docker exec signoz-clickhouse clickhouse-client --query "SELECT count() as total FROM signoz_logs.logs_v2 WHERE timestamp >= $($start24h)000000 AND timestamp <= $($now)000000"
Write-Host "Logs in ClickHouse within time range: $chResult" -ForegroundColor Green
