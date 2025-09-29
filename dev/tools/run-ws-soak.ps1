# Run Dashboard WS soak test with configurable env
param(
    [string]$BaseUrl,
    [int]$Sessions = 50,
    [int]$Minutes = 3
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null
$urls = Resolve-TansuBaseUrls -PreferLoopbackForGateway

if (-not $PSBoundParameters.ContainsKey('BaseUrl') -or [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = $urls.PublicBaseUrl
}

Write-Host "Running WS soak: BaseUrl=$BaseUrl, Sessions=$Sessions, Minutes=$Minutes"

$env:GATEWAY_BASE_URL = $urls.GatewayBaseUrl
$env:RUN_SOAK = '1'
$env:SOAK_SESSIONS = [string]$Sessions
$env:SOAK_MINUTES = [string]$Minutes

${proj} = Join-Path $PSScriptRoot '..\..\tests\TansuCloud.E2E.Tests\TansuCloud.E2E.Tests.csproj'
dotnet test $proj -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.DashboardWebsocketSoak.Soak_50_Sessions_3_Minutes

if ($LASTEXITCODE -ne 0) {
    throw "WS soak failed with exit code $LASTEXITCODE"
}

Write-Host "WS soak completed successfully." -ForegroundColor Green
