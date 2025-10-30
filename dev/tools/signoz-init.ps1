#!/usr/bin/env pwsh
#
# signoz-init.ps1
# Automatically initialize SigNoz with admin user if not already set up
#

param(
    [string]$SigNozUrl = "http://localhost:8080",
    [string]$AdminEmail,
    [string]$AdminPassword,
    [string]$AdminName = "TansuCloud Admin",
    [string]$OrgName = "TansuCloud",
    [int]$MaxRetries = 30,
    [int]$RetryDelaySeconds = 2
)

$ErrorActionPreference = "Stop"

function Wait-ForSigNoz {
    param([string]$Url, [int]$MaxAttempts, [int]$Delay)
    
    Write-Host "Waiting for SigNoz to be ready..."
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "$Url/api/v1/version" -Method Get -TimeoutSec 5 -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                Write-Host "✓ SigNoz is ready"
                return $true
            }
        } catch {
            # Ignore errors, service not ready yet
        }
        
        if ($i -lt $MaxAttempts) {
            Write-Host "  Attempt $i/$MaxAttempts - waiting ${Delay}s..."
            Start-Sleep -Seconds $Delay
        }
    }
    
    Write-Error "SigNoz did not become ready after $MaxAttempts attempts"
    return $false
}

function Test-SigNozInitialized {
    param([string]$Url, [string]$Email)
    
    try {
        $uri = "$Url/api/v2/sessions/context?email=$([System.Web.HttpUtility]::UrlEncode($Email))"
        $response = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 5
        
        if ($response.data.exists -eq $true -and $response.data.orgs.Count -gt 0) {
            return $true
        }
    } catch {
        # Likely 404 or other error, not initialized
    }
    
    return $false
}

function Initialize-SigNoz {
    param(
        [string]$Url,
        [string]$Email,
        [string]$Password,
        [string]$Name,
        [string]$Org
    )
    
    Write-Host "Registering first admin user in SigNoz..."
    
    $body = @{
        email = $Email
        name = $Name
        password = $Password
        orgName = $Org
        orgDisplayName = $Org
    } | ConvertTo-Json
    
    try {
        $response = Invoke-RestMethod `
            -Uri "$Url/api/v1/register" `
            -Method Post `
            -Body $body `
            -ContentType "application/json" `
            -TimeoutSec 10
        
        Write-Host "✓ SigNoz initialized successfully"
        Write-Host "  Admin user: $Email"
        Write-Host "  Organization: $Org"
        return $true
    } catch {
        $statusCode = $_.Exception.Response.StatusCode.Value__
        if ($statusCode -eq 400) {
            # Check if it's because setup is already complete
            $errorBody = $_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction SilentlyContinue
            if ($errorBody.error -match "self-registration is disabled") {
                Write-Host "✓ SigNoz already initialized"
                return $true
            }
        }
        
        Write-Error "Failed to initialize SigNoz: $_"
        return $false
    }
}

# Main execution
try {
    # Load credentials from environment if not provided
    if ([string]::IsNullOrEmpty($AdminEmail)) {
        $AdminEmail = $env:SIGNOZ_API_EMAIL
    }
    if ([string]::IsNullOrEmpty($AdminPassword)) {
        $AdminPassword = $env:SIGNOZ_API_PASSWORD
    }
    
    # Validate required parameters
    if ([string]::IsNullOrEmpty($AdminEmail) -or [string]::IsNullOrEmpty($AdminPassword)) {
        Write-Error "Admin credentials required. Set SIGNOZ_API_EMAIL and SIGNOZ_API_PASSWORD environment variables or pass as parameters."
        exit 1
    }
    
    # Wait for SigNoz to be ready
    if (-not (Wait-ForSigNoz -Url $SigNozUrl -MaxAttempts $MaxRetries -Delay $RetryDelaySeconds)) {
        exit 1
    }
    
    # Check if already initialized
    if (Test-SigNozInitialized -Url $SigNozUrl -Email $AdminEmail) {
        Write-Host "✓ SigNoz is already initialized with user: $AdminEmail"
        exit 0
    }
    
    # Initialize SigNoz
    if (Initialize-SigNoz -Url $SigNozUrl -Email $AdminEmail -Password $AdminPassword -Name $AdminName -Org $OrgName) {
        Write-Host ""
        Write-Host "═══════════════════════════════════════════════════════"
        Write-Host "  SigNoz Setup Complete"
        Write-Host "═══════════════════════════════════════════════════════"
        Write-Host "  Admin Email: $AdminEmail"
        Write-Host "  Organization: $OrgName"
        Write-Host ""
        Write-Host "  Access observability via TansuCloud Dashboard:"
        Write-Host "  http://your-domain.com/dashboard/admin/observability"
        Write-Host "═══════════════════════════════════════════════════════"
        Write-Host ""
        exit 0
    } else {
        exit 1
    }
} catch {
    Write-Error "Unexpected error: $_"
    exit 1
}
