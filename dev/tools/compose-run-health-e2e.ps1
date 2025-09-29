#requires -Version 7.0
<#
.SYNOPSIS
  Bring up Docker Compose stack, wait until healthy, then run health E2E tests.

.DESCRIPTION
  This dev helper script ensures the compose stack is running and healthy before executing the
  HealthEndpointsE2E test class via `dotnet test`.

.PARAMETER ComposeFile
  Path to docker compose file. Defaults to ./docker-compose.yml.

.PARAMETER NoBuild
  Skip building the .sln before tests.

.PARAMETER UpOnly
  Only start and wait for the stack; do not run tests.

.PARAMETER TimeoutSeconds
  Max seconds to wait for readiness (default 600).

.EXAMPLE
  pwsh -File ./dev/tools/compose-run-health-e2e.ps1

.EXAMPLE
  pwsh -File ./dev/tools/compose-run-health-e2e.ps1 -NoBuild -TimeoutSeconds 900
#>

[CmdletBinding()]
param(
  [string]$ComposeFile = "$(Join-Path $PSScriptRoot '..' '..' 'docker-compose.yml')",
  [switch]$NoBuild,
  [switch]$UpOnly,
  [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Resolve-TansuRepoRoot
Import-TansuDotEnv | Out-Null
$envFile = Join-Path $repoRoot '.env'

function Write-Section {
  param([string]$Text)
  Write-Host "`n=== $Text ===" -ForegroundColor Cyan
}

function Invoke-ComposeUp {
  Write-Section 'docker compose up -d (infra + apps)'
  $composeArgs = @('--env-file', $envFile, '-f', $ComposeFile, 'up', '-d', '--build')
  docker compose @composeArgs | Write-Host
}

function Wait-ComposeHealthy {
  Write-Section 'Waiting for services to be healthy'
  $waitScript = Join-Path $PSScriptRoot 'compose-wait-healthy.ps1'
  & pwsh -NoProfile -File $waitScript -ComposeFile $ComposeFile -EnvFile $envFile -TimeoutSeconds $TimeoutSeconds
}

function Invoke-Build {
  Write-Section 'dotnet build'
  dotnet build ./TansuCloud.sln -c Debug | Write-Host
}

function Invoke-HealthE2E {
  Write-Section 'dotnet test (HealthEndpointsE2E)'
  dotnet test ./tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.HealthEndpointsE2E
}

# Main
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
  throw 'Docker CLI is not available. Please install/start Docker Desktop.'
}

if (-not (Test-Path -LiteralPath $ComposeFile)) {
  throw "Compose file not found: $ComposeFile"
}

Invoke-ComposeUp
Wait-ComposeHealthy

if ($UpOnly) { Write-Section 'UpOnly requested. Exiting after readiness.'; exit 0 }

if (-not $NoBuild) { Invoke-Build }

Invoke-HealthE2E
