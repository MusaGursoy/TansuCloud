#requires -Version 7.0
<#
.SYNOPSIS
  Waits until selected Docker Compose services are healthy (or running if no healthcheck) before proceeding.

.DESCRIPTION
  This script inspects containers created by docker compose and waits until they are all ready.
  - If a container defines a Healthcheck, it must reach Status=healthy.
  - If no healthcheck is defined, the container must be in State=running.

.PARAMETER ComposeFile
  Path to the docker compose file. Defaults to ./docker-compose.yml.

.PARAMETER Services
  The compose service names to wait for. If omitted, a sensible default set for TansuCloud will be used.

.PARAMETER TimeoutSeconds
  Max seconds to wait overall. Default 600 (10 minutes).

.PARAMETER PollSeconds
  Poll interval. Default 5 seconds.

.EXAMPLE
  pwsh -File ./dev/tools/compose-wait-healthy.ps1

.EXAMPLE
  pwsh -File ./dev/tools/compose-wait-healthy.ps1 -Services gateway,identity,dashboard,db,storage

.NOTES
  This script is intended for local dev/E2E. It requires Docker Desktop / docker compose v2.
#>

[CmdletBinding()]
param(
  [string]$ComposeFile = "$(Join-Path $PSScriptRoot '..' '..' 'docker-compose.yml')",
  [string[]]$Services,
  [string]$EnvFile,
  [int]$TimeoutSeconds = 600,
  [int]$PollSeconds = 5
)

. (Join-Path $PSScriptRoot 'common.ps1')
$repoRoot = Resolve-TansuRepoRoot
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
  $EnvFile = Join-Path $repoRoot '.env'
}
Import-TansuDotEnv | Out-Null

function Write-Section {
  param([string]$Text)
  Write-Host "`n=== $Text ===" -ForegroundColor Cyan
}

function Test-DockerAvailable {
  try {
    docker --version | Out-Null
    return $true
  } catch {
    return $false
  }
}

function Get-ContainerId([string]$svc) {
  try {
    $ids = Invoke-TansuCompose -ComposeFile $ComposeFile -EnvFile $EnvFile ps '-q' $svc
  }
  catch {
    return $null
  }

  if (-not $ids) {
    return $null
  }

  $id = ($ids | Select-Object -Last 1).Trim()
  if ([string]::IsNullOrWhiteSpace($id)) {
    return $null
  }

  return $id
}

function Get-ContainerStatus([string]$containerId) {
  # Returns a PSCustomObject: @{ Raw = <raw>; HasHealth = $true/$false; Health = 'healthy'|'starting'|'unhealthy'|$null; State = 'running'|'exited'|'created'|... }
  $fmt = '{{json .State}}'
  $raw = docker inspect -f $fmt $containerId 2>$null
  if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
  try {
    $state = $raw | ConvertFrom-Json
  } catch {
    return $null
  }
  $hasHealth = $false
  $healthStatus = $null
  if ($state.Health) {
    $hasHealth = $true
    $healthStatus = $state.Health.Status
  }
  [pscustomobject]@{
    Raw       = $raw
    HasHealth = $hasHealth
    Health    = $healthStatus
    State     = $state.Status
  }
}

function Test-ServiceReady([string]$svc) {
  $cid = Get-ContainerId $svc
  if (-not $cid) { return $false }
  $st = Get-ContainerStatus $cid
  if (-not $st) { return $false }
  if ($st.HasHealth) { return $st.Health -eq 'healthy' }
  return $st.State -eq 'running'
}

function Get-DefaultServices {
  # Minimal core set needed for our E2E health tests; SigNoz stack is optional for these tests
  @(
    'postgres',
    'redis',
    'pgcat',
    'pgcat-config',
    'identity',
    'dashboard',
    'db',
    'storage',
    'gateway'
  )
}

if (-not (Test-DockerAvailable)) {
  Write-Error 'Docker CLI is not available. Please install/start Docker Desktop.'
  exit 1
}

if (-not (Test-Path -LiteralPath $ComposeFile)) {
  Write-Error "Compose file not found: $ComposeFile"
  exit 1
}

if (-not $Services -or $Services.Count -eq 0) {
  $Services = Get-DefaultServices
}

Write-Section "Compose readiness check"
Write-Host ("Compose file: {0}" -f (Resolve-Path $ComposeFile))
Write-Host ("Services: {0}" -f ($Services -join ', '))
Write-Host ("Timeout: {0}s, Poll: {1}s" -f $TimeoutSeconds, $PollSeconds)

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$pending = [System.Collections.Generic.HashSet[string]]::new()
$Services | ForEach-Object { [void]$pending.Add($_) }

# Preflight: surface missing containers early (will be retried within loop)
foreach ($svc in $Services) {
  $cid = Get-ContainerId $svc
  if (-not $cid) {
    Write-Host ("- {0}: container not found yet" -f $svc) -ForegroundColor Yellow
  } else {
    $st = Get-ContainerStatus $cid
    if ($st) {
      $display = if ($st.HasHealth) { "health=$($st.Health)" } else { "state=$($st.State)" }
      Write-Host ("- {0}: {1}" -f $svc, $display)
    }
  }
}

while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
  Start-Sleep -Seconds $PollSeconds
  foreach ($svc in @($pending)) {
    if (Test-ServiceReady $svc) {
      Write-Host ("✔ {0} is ready" -f $svc) -ForegroundColor Green
      [void]$pending.Remove($svc)
    } else {
      Write-Host ("… waiting for {0}" -f $svc)
    }
  }
}

if ($pending.Count -gt 0) {
  Write-Error ("Timeout waiting for services to be ready: {0}" -f ($pending -join ', '))
  # Dump last-known status for pending
  foreach ($svc in $pending) {
    $cid = Get-ContainerId $svc
    if ($cid) {
      $st = Get-ContainerStatus $cid
      if ($st) {
        $disp = if ($st.HasHealth) { "health=$($st.Health)" } else { "state=$($st.State)" }
        Write-Host ("- {0}: {1}" -f $svc, $disp) -ForegroundColor Yellow
      } else {
        Write-Host ("- {0}: container present but status unknown" -f $svc) -ForegroundColor Yellow
      }
    } else {
      Write-Host ("- {0}: no container" -f $svc) -ForegroundColor Yellow
    }
  }
  exit 2
}

Write-Section "All services ready"
$Services | ForEach-Object { Write-Host ("- {0}" -f $_) -ForegroundColor Green }
exit 0
