# Run Dashboard WS soak test with configurable env
param(
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [int]$Sessions = 50,
    [int]$Minutes = 3
)

$ErrorActionPreference = 'Stop'

Write-Host "Running WS soak: BaseUrl=$BaseUrl, Sessions=$Sessions, Minutes=$Minutes"

$env:GATEWAY_BASE_URL = $BaseUrl
$env:RUN_SOAK = '1'
$env:SOAK_SESSIONS = [string]$Sessions
$env:SOAK_MINUTES = [string]$Minutes

${proj} = Join-Path $PSScriptRoot '..\..\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj'
dotnet test $proj -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.DashboardWebsocketSoak.Soak_50_Sessions_3_Minutes

if ($LASTEXITCODE -ne 0) {
    throw "WS soak failed with exit code $LASTEXITCODE"
}

Write-Host "WS soak completed successfully." -ForegroundColor Green
