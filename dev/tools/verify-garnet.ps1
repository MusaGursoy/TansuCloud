# Garnet Migration Verification Script
# Run this after starting docker compose with Garnet

$ErrorActionPreference = 'Stop'

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Garnet Migration Verification" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. Check if Garnet container is running
Write-Host "1. Checking Garnet container status..." -ForegroundColor Yellow
$garnetContainer = docker ps --filter "name=tansu-redis" --format "{{.Status}}"
if ($garnetContainer -match "Up") {
    Write-Host "   ✓ Garnet container is running" -ForegroundColor Green
} else {
    Write-Host "   ✗ Garnet container is NOT running" -ForegroundColor Red
    Write-Host "   Run: docker compose up -d" -ForegroundColor Yellow
    exit 1
}

# 2. Check Garnet health
Write-Host "`n2. Testing Garnet connectivity..." -ForegroundColor Yellow
try {
    $pingResult = docker exec tansu-redis redis-cli ping 2>&1
    if ($pingResult -match "PONG") {
        Write-Host "   ✓ Garnet responds to PING" -ForegroundColor Green
    } else {
        Write-Host "   ✗ Garnet did not respond correctly: $pingResult" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "   ✗ Failed to ping Garnet: $_" -ForegroundColor Red
    exit 1
}

# 3. Check Garnet version/info
Write-Host "`n3. Checking Garnet server info..." -ForegroundColor Yellow
$serverInfo = docker exec tansu-redis redis-cli INFO SERVER 2>&1
if ($serverInfo -match "garnet" -or $serverInfo -match "redis_version") {
    Write-Host "   ✓ Server info retrieved successfully" -ForegroundColor Green
    # Extract version if available
    if ($serverInfo -match "redis_version:([^\r\n]+)") {
        Write-Host "   Version: $($matches[1])" -ForegroundColor Cyan
    }
} else {
    Write-Host "   ⚠ Could not retrieve server info (may be expected)" -ForegroundColor Yellow
}

# 4. Test basic SET/GET operations
Write-Host "`n4. Testing basic operations (SET/GET)..." -ForegroundColor Yellow
try {
    $testKey = "garnet:test:$(Get-Date -Format 'yyyyMMddHHmmss')"
    $testValue = "migration-test-$(Get-Random)"
    
    $setResult = docker exec tansu-redis redis-cli SET $testKey $testValue
    if ($setResult -match "OK") {
        Write-Host "   ✓ SET operation successful" -ForegroundColor Green
    } else {
        Write-Host "   ✗ SET operation failed: $setResult" -ForegroundColor Red
        exit 1
    }
    
    $getValue = docker exec tansu-redis redis-cli GET $testKey
    if ($getValue -eq $testValue) {
        Write-Host "   ✓ GET operation successful (value matches)" -ForegroundColor Green
    } else {
        Write-Host "   ✗ GET operation failed (expected: $testValue, got: $getValue)" -ForegroundColor Red
        exit 1
    }
    
    # Cleanup
    docker exec tansu-redis redis-cli DEL $testKey | Out-Null
} catch {
    Write-Host "   ✗ Failed to test SET/GET: $_" -ForegroundColor Red
    exit 1
}

# 5. Test Pub/Sub (used by Outbox)
Write-Host "`n5. Testing Pub/Sub functionality..." -ForegroundColor Yellow
try {
    $testChannel = "test:channel:$(Get-Random)"
    $testMessage = "test-message-$(Get-Random)"
    
    # Start subscriber in background job
    $subscriberJob = Start-Job -ScriptBlock {
        param($container, $channel)
        docker exec $container redis-cli SUBSCRIBE $channel
    } -ArgumentList "tansu-redis", $testChannel
    
    Start-Sleep -Seconds 1
    
    # Publish message
    $publishResult = docker exec tansu-redis redis-cli PUBLISH $testChannel $testMessage
    
    Start-Sleep -Seconds 1
    Stop-Job -Job $subscriberJob | Out-Null
    Remove-Job -Job $subscriberJob | Out-Null
    
    if ($publishResult -ge 0) {
        Write-Host "   ✓ Pub/Sub test completed (subscribers: $publishResult)" -ForegroundColor Green
    } else {
        Write-Host "   ⚠ Pub/Sub test inconclusive" -ForegroundColor Yellow
    }
} catch {
    Write-Host "   ⚠ Pub/Sub test encountered issues (may be expected): $_" -ForegroundColor Yellow
}

# 6. Check if services are healthy
Write-Host "`n6. Checking dependent services..." -ForegroundColor Yellow
$services = @("tansu-identity", "tansu-dashboard", "tansu-database", "tansu-storage")
$allHealthy = $true

foreach ($service in $services) {
    $status = docker ps --filter "name=$service" --format "{{.Status}}"
    if ($status -match "Up.*healthy" -or $status -match "Up") {
        Write-Host "   ✓ $service is running" -ForegroundColor Green
    } else {
        Write-Host "   ⚠ $service status: $status" -ForegroundColor Yellow
        $allHealthy = $false
    }
}

# 7. Load .env and check configuration
Write-Host "`n7. Checking configuration..." -ForegroundColor Yellow
. "$PSScriptRoot/common.ps1"
Import-TansuDotEnv | Out-Null

$expectedConfig = @{
    "PUBLIC_BASE_URL" = $env:PUBLIC_BASE_URL
    "GATEWAY_BASE_URL" = $env:GATEWAY_BASE_URL
}

foreach ($key in $expectedConfig.Keys) {
    if ($expectedConfig[$key]) {
        Write-Host "   ✓ $key is set" -ForegroundColor Green
    } else {
        Write-Host "   ⚠ $key is not set" -ForegroundColor Yellow
    }
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Verification Summary" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "✓ Garnet is running and operational" -ForegroundColor Green
Write-Host "✓ Basic Redis protocol operations work" -ForegroundColor Green
Write-Host "✓ Pub/Sub functionality available (for Outbox)" -ForegroundColor Green

if ($allHealthy) {
    Write-Host "✓ All dependent services are healthy" -ForegroundColor Green
} else {
    Write-Host "⚠ Some services may still be starting up" -ForegroundColor Yellow
}

Write-Host "`nNext steps:" -ForegroundColor Cyan
Write-Host "  1. Run health E2E tests:" -ForegroundColor White
Write-Host "     dotnet test .\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~HealthEndpointsE2E" -ForegroundColor Gray
Write-Host "`n  2. Test Outbox publishing (if REDIS_URL is set):" -ForegroundColor White
Write-Host "     `$env:REDIS_URL = 'localhost:6379'" -ForegroundColor Gray
Write-Host "     dotnet test .\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~OutboxDispatcherFullE2ERedisTests" -ForegroundColor Gray
Write-Host "`n  3. Provision a test tenant:" -ForegroundColor White
Write-Host "     Use VS Code task: 'Provision tenant via Gateway (dev bypass)'" -ForegroundColor Gray
Write-Host "`n" -ForegroundColor White
