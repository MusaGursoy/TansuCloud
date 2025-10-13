param(
    [int]$Count = 10,
    [string]$HostName = 'gateway',
    [string]$Service = 'tansu.dashboard',
    [string]$Environment = 'Development',
    [string]$IngestionUrl = 'http://127.0.0.1:5279/api/logs/report',
    [string]$ApiKey = $env:TELEMETRY__INGESTION__APIKEY
)

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = 'dev-telemetry-api-key-1234567890'
}

for ($i = 1; $i -le $Count; $i++) {
    $timestamp = (Get-Date).ToUniversalTime().ToString('o')
    $hash = "automation-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"

    $body = @{
    host = $HostName
        environment = $Environment
        service = $Service
        severityThreshold = 'Warning'
        windowMinutes = 5
        maxItems = 10
        items = @(
            @{
                timestamp = $timestamp
                count = 1
                kind = 'log'
                level = 'Error'
                message = "Synthetic telemetry event $i"
                category = 'Automation'
                service = $Service
                environment = $Environment
                host = $HostName
                templateHash = $hash
            }
        )
    }

    $json = $body | ConvertTo-Json -Depth 5
    $headers = @{ Authorization = "Bearer $ApiKey" }

    try {
        $response = Invoke-RestMethod -Method Post -Uri $IngestionUrl -Headers $headers -Body $json -ContentType 'application/json'
        Write-Host ("{0:00}: accepted={1}" -f $i, $response.accepted)
    }
    catch {
        Write-Warning ("{0:00}: failed -> {1}" -f $i, $_)
    }

    Start-Sleep -Milliseconds 200
}
