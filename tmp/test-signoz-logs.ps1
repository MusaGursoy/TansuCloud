$ErrorActionPreference = 'Stop'

# Time range (milliseconds)
$startMs = 1762123538801
$endMs = 1762209938801

$body = @{
    schemaVersion = "v1"
    start = $startMs
    end = $endMs
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

Write-Host "Query payload:"
Write-Host $body

$response = Invoke-RestMethod -Uri "http://127.0.0.1:3301/api/v5/query_range" -Method Post `
    -ContentType "application/json" `
    -Body $body

Write-Host "`nResponse:"
$response | ConvertTo-Json -Depth 10 | Write-Host
