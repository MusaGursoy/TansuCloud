# Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

<#
.SYNOPSIS
    Provisions the Grafana database using TansuCloud.Database service API.

.DESCRIPTION
    This script calls the Database service provisioning endpoint to create the 'grafana'
    database in the PostgreSQL instance. It's designed to run before Grafana starts,
    either as an init container in Docker Compose or as a one-time setup script.

.PARAMETER GatewayBaseUrl
    The base URL of the Gateway service (default: http://127.0.0.1:8080)

.PARAMETER ProvisionKey
    The provisioning key for authentication (default: from PROVISION_KEY env var or 'letmein')

.PARAMETER MaxRetries
    Maximum number of retries if Database service is not ready (default: 30)

.PARAMETER RetryDelaySeconds
    Delay between retries in seconds (default: 2)

.EXAMPLE
    .\provision-grafana-db.ps1

.EXAMPLE
    .\provision-grafana-db.ps1 -GatewayBaseUrl "http://localhost:8080" -ProvisionKey "secret123"
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$GatewayBaseUrl = $env:GATEWAY_BASE_URL ?? "http://127.0.0.1:8080",
    
    [Parameter()]
    [string]$ProvisionKey = $env:PROVISION_KEY ?? "letmein",
    
    [Parameter()]
    [int]$MaxRetries = 30,
    
    [Parameter()]
    [int]$RetryDelaySeconds = 2
)

$ErrorActionPreference = 'Stop'

Write-Host "🔧 Grafana Database Provisioning Script" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host ""

# Construct API endpoint
$provisioningEndpoint = "$($GatewayBaseUrl.TrimEnd('/'))/db/api/provisioning/databases"
Write-Host "📍 Provisioning endpoint: $provisioningEndpoint" -ForegroundColor Gray

# Request body
$body = @{
    databaseName = "grafana"
    purpose = "observability"
    owner = "platform"
} | ConvertTo-Json

# Headers
$headers = @{
    "X-Provision-Key" = $ProvisionKey
    "Content-Type" = "application/json"
}

# Wait for Database service to be ready
Write-Host "⏳ Waiting for Database service to be ready..." -ForegroundColor Yellow
$attempt = 0
$serviceReady = $false

while ($attempt -lt $MaxRetries -and -not $serviceReady) {
    $attempt++
    
    try {
        # Check if Database service health endpoint responds
        $healthUrl = "$($GatewayBaseUrl.TrimEnd('/'))/db/health"
        $healthResponse = Invoke-WebRequest -Uri $healthUrl -Method Get -TimeoutSec 5 -ErrorAction Stop
        
        if ($healthResponse.StatusCode -eq 200) {
            $serviceReady = $true
            Write-Host "✅ Database service is ready (attempt $attempt/$MaxRetries)" -ForegroundColor Green
        }
    }
    catch {
        if ($attempt -eq $MaxRetries) {
            Write-Host "❌ Database service did not become ready after $MaxRetries attempts" -ForegroundColor Red
            Write-Host "   Error: $($_.Exception.Message)" -ForegroundColor Red
            exit 1
        }
        
        Write-Host "   Attempt $attempt/$MaxRetries failed, retrying in $RetryDelaySeconds seconds..." -ForegroundColor Gray
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

# Provision Grafana database
Write-Host ""
Write-Host "🗄️  Provisioning 'grafana' database..." -ForegroundColor Cyan

try {
    $response = Invoke-RestMethod -Uri $provisioningEndpoint -Method Post -Headers $headers -Body $body -ErrorAction Stop
    
    Write-Host "✅ Grafana database provisioned successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "📊 Response:" -ForegroundColor Gray
    $response | ConvertTo-Json -Depth 5 | Write-Host
    
    exit 0
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    
    # 409 Conflict means database already exists - that's OK
    if ($statusCode -eq 409) {
        Write-Host "ℹ️  Grafana database already exists (HTTP 409 Conflict)" -ForegroundColor Yellow
        Write-Host "   This is expected if running the script multiple times." -ForegroundColor Gray
        exit 0
    }
    
    # Other errors are failures
    Write-Host "❌ Failed to provision Grafana database" -ForegroundColor Red
    Write-Host "   Status Code: $statusCode" -ForegroundColor Red
    Write-Host "   Error: $($_.Exception.Message)" -ForegroundColor Red
    
    if ($_.ErrorDetails.Message) {
        Write-Host "   Details: $($_.ErrorDetails.Message)" -ForegroundColor Red
    }
    
    exit 1
}
