# Run containerized E2E tests with proper service startup
# This ensures all services are healthy before running tests

$ErrorActionPreference = 'Stop'

# Import common utilities
. "$PSScriptRoot/common.ps1"

Write-Host "=== Containerized E2E Test Runner ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Load environment variables
Write-Host "[1/4] Loading environment variables..." -ForegroundColor Yellow
Import-TansuDotEnv | Out-Null

# Step 2: Start all services (not just test profile)
Write-Host "[2/4] Starting all services..." -ForegroundColor Yellow
docker compose up -d --build

# Step 3: Wait for gateway health check
Write-Host "[3/4] Waiting for gateway to be healthy..." -ForegroundColor Yellow
$maxAttempts = 60
$attempt = 0
$healthy = $false

while (-not $healthy -and $attempt -lt $maxAttempts) {
    $attempt++
    try {
        $healthStatus = docker inspect tansu-gateway --format='{{.State.Health.Status}}' 2>$null
        if ($healthStatus -eq "healthy") {
            $healthy = $true
            Write-Host "Gateway is healthy!" -ForegroundColor Green
        } else {
            Write-Host "  Attempt $attempt/$maxAttempts - Gateway status: $healthStatus" -ForegroundColor Gray
            Start-Sleep -Seconds 2
        }
    } catch {
        Write-Host "  Attempt $attempt/$maxAttempts - Gateway not ready yet" -ForegroundColor Gray
        Start-Sleep -Seconds 2
    }
}

if (-not $healthy) {
    Write-Host "ERROR: Gateway failed to become healthy after $maxAttempts attempts" -ForegroundColor Red
    Write-Host "Checking gateway logs:" -ForegroundColor Yellow
    docker logs tansu-gateway --tail 50
    exit 1
}

# Step 4: Run tests
Write-Host "[4/4] Running E2E tests..." -ForegroundColor Yellow
Write-Host ""

# Remove old test container if it exists
docker rm -f tansu-e2e-tests 2>$null | Out-Null

# Run tests with proper network attachment
$testResult = docker compose --profile testing up --build --abort-on-container-exit --exit-code-from e2e-tests e2e-tests

# Capture exit code
$exitCode = $LASTEXITCODE

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "=== All tests passed! ===" -ForegroundColor Green
} else {
    Write-Host "=== Tests failed with exit code $exitCode ===" -ForegroundColor Red
}

exit $exitCode
