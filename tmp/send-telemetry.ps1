$timestamp = (Get-Date).ToUniversalTime().ToString('o')
$body = @{
    host = 'gateway'
    environment = 'Development'
    service = 'tansu.dashboard'
    severityThreshold = 'Warning'
    windowMinutes = 5
    maxItems = 10
    items = @(
        @{
            timestamp = $timestamp
            count = 1
            kind = 'log'
            level = 'Error'
            message = 'Test telemetry event generated from automation'
            category = 'PlaywrightTest'
            service = 'tansu.dashboard'
            environment = 'Development'
            host = 'gateway'
            templateHash = 'automation-sample-001'
        }
    )
}
$json = $body | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5279/api/logs/report' -Headers @{ Authorization = 'Bearer dev-telemetry-api-key-1234567890' } -Body $json -ContentType 'application/json'
