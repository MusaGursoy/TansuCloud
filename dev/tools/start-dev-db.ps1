param(
  [string]$ContainerName = 'tansudbpg',
  [string]$VolumeName = 'tansu-pgdata',
  [int]$HostPort = 5432
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host $msg -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host $msg -ForegroundColor Yellow }

# Verify Docker
try { docker version | Out-Null } catch { throw 'Docker is not available. Please install/start Docker Desktop and retry.' }

# Ensure volume exists
$volumes = docker volume ls --format '{{.Name}}'
if (-not ($volumes -split "`n" | Where-Object { $_ -eq $VolumeName })) {
  Write-Info "Creating volume '$VolumeName'"
  docker volume create $VolumeName | Out-Null
}

# Resolve init scripts path (repo/dev/db-init)
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
$dbInit = Join-Path $repoRoot 'db-init'
if (-not (Test-Path $dbInit)) { throw "Init path not found: $dbInit" }

# Normalize Windows path for Docker mount
$dbInitDocker = ($dbInit -replace '\\','/')

# Check if container exists
$exists = docker ps -a --format '{{.Names}}' | Where-Object { $_ -eq $ContainerName }
if ($exists) {
  # Start if not running
  $running = docker ps --format '{{.Names}}' | Where-Object { $_ -eq $ContainerName }
  if (-not $running) {
    Write-Info "Starting existing container '$ContainerName'"
    docker start $ContainerName | Out-Null
  } else {
    Write-Info "Container '$ContainerName' is already running"
  }
} else {
  Write-Info "Creating and starting container '$ContainerName' on port $HostPort"
  # If 5432 is in use, user should change HostPort param
  docker run -d --name $ContainerName `
    -p "${HostPort}:5432" `
    -e POSTGRES_USER=postgres `
    -e POSTGRES_PASSWORD=postgres `
    -v "${VolumeName}:/var/lib/postgresql/data" `
    -v "${dbInitDocker}:/docker-entrypoint-initdb.d:ro" `
    citusdata/citus:latest | Out-Null
}

Write-Info 'Waiting for PostgreSQL to become ready...'
Start-Sleep -Seconds 5
docker ps --format 'table {{.Names}}`t{{.Status}}`t{{.Ports}}' | Where-Object { $_ -match $ContainerName } | Write-Host
