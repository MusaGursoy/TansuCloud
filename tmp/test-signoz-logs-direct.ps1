$ErrorActionPreference = 'Stop'

# Time range (milliseconds) - last 24 hours
$now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$start24h = $now - (24 * 3600 * 1000)

Write-Host "Time range: $start24h to $now"
Write-Host "Duration: 24 hours"
Write-Host ""

# Test 1: Basic log search query (our current implementation)
$body1 = @{
    schemaVersion = "v1"
    start = $start24h
    end = $now
    requestType = "raw"
    compositeQuery = @{
        queries = @(
            @{
                type = "builder_query"
                spec = @{
                    name = "A"
                    signal = "logs"
                    disabled = $false
                    aggregations = @()
                    filter = @{ expression = "" }
                    limit = 100
                    offset = 0
                    having = @{ expression = "" }
                }
            }
        )
    }
    formatOptions = @{ 
        formatTableResultForUI = $true
        fillGaps = $false
    }
    variables = @{}
} | ConvertTo-Json -Depth 10

Write-Host "=== Test 1: Basic Query (Current Implementation) ===" -ForegroundColor Cyan
Write-Host $body1
Write-Host ""

try {
    $response1 = docker exec signoz curl -s -X POST `
        -H "Content-Type: application/json" `
        -d $body1 `
        "http://localhost:8080/api/v5/query_range"
    
    Write-Host "Response:" -ForegroundColor Green
    $response1 | ConvertFrom-Json | ConvertTo-Json -Depth 10 | Write-Host
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Test 2: Simplified Query ===" -ForegroundColor Cyan

# Test 2: Simplified query without formatOptions
$body2 = @{
    start = $start24h
    end = $now
    compositeQuery = @{
        queryType = "builder"
        builderQueries = @{
            A = @{
                dataSource = "logs"
                queryName = "A"
                aggregateOperator = "noop"
                filters = @{
                    items = @()
                    op = "AND"
                }
                limit = 100
                offset = 0
            }
        }
    }
} | ConvertTo-Json -Depth 10

Write-Host $body2
Write-Host ""

try {
    $response2 = docker exec signoz curl -s -X POST `
        -H "Content-Type: application/json" `
        -d $body2 `
        "http://localhost:8080/api/v5/query_range"
    
    Write-Host "Response:" -ForegroundColor Green
    $response2 | ConvertFrom-Json | ConvertTo-Json -Depth 10 | Write-Host
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== ClickHouse Verification ===" -ForegroundColor Cyan
$clickhouseQuery = "SELECT count() as total, min(timestamp) as oldest_nano, max(timestamp) as newest_nano FROM signoz_logs.logs_v2 WHERE timestamp >= $($start24h)000000 AND timestamp <= $($now)000000"
Write-Host "Query: $clickhouseQuery"

$chResult = docker exec signoz-clickhouse clickhouse-client --query $clickhouseQuery
Write-Host "ClickHouse result: $chResult" -ForegroundColor Green
