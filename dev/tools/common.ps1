#requires -Version 7.0
<#+
.SYNOPSIS
  Helper utilities shared across Tansu Cloud dev scripts.

.DESCRIPTION
  Provides helper functions such as Import-TansuDotEnv to hydrate environment variables
  from the repository's .env file (or a supplied path) without overwriting existing values
  unless explicitly requested.
#>

function Resolve-TansuRepoRoot {
  param()

  $rootCandidate = Join-Path $PSScriptRoot '..'
  $rootCandidate = Join-Path $rootCandidate '..'
  return (Get-Item -LiteralPath $rootCandidate).FullName
}

function Import-TansuDotEnv {
  [CmdletBinding()]
  param(
    [string]$Path,
    [switch]$Overwrite
  )

  if ([string]::IsNullOrWhiteSpace($Path)) {
    $repoRoot = Resolve-TansuRepoRoot
    $Path = Join-Path $repoRoot '.env'
  }

  if (-not (Test-Path -LiteralPath $Path)) {
    return $false
  }

  $updatedAny = $false
  foreach ($rawLine in (Get-Content -LiteralPath $Path)) {
    $line = $rawLine.Trim()

    if (-not [string]::IsNullOrWhiteSpace($line) -and $line.StartsWith('export ')) {
      $line = $line.Substring(7).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line.StartsWith('#')) { continue }

    $separator = $line.IndexOf('=')
    if ($separator -lt 1) { continue }

    $key = $line.Substring(0, $separator).Trim()
    $value = $line.Substring($separator + 1).Trim()

    if ([string]::IsNullOrWhiteSpace($key)) { continue }

    if ($value.StartsWith('"') -and $value.EndsWith('"')) {
      $value = $value.Substring(1, $value.Length - 2)
    }
    elseif ($value.StartsWith("'") -and $value.EndsWith("'")) {
      $value = $value.Substring(1, $value.Length - 2)
    }

    $existing = [Environment]::GetEnvironmentVariable($key, [EnvironmentVariableTarget]::Process)
    if (-not $Overwrite -and -not [string]::IsNullOrWhiteSpace($existing)) {
      continue
    }

    [Environment]::SetEnvironmentVariable($key, $value, [EnvironmentVariableTarget]::Process)
    $updatedAny = $true
  }

  return $updatedAny
}

function Normalize-TansuBaseUrl {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)]
    [string]$Url,
    [switch]$PreferLoopback
  )

  if ([string]::IsNullOrWhiteSpace($Url)) {
    return $Url
  }

  $trimmed = $Url.Trim()
  if ($trimmed.EndsWith('/')) {
    $trimmed = $trimmed.TrimEnd('/')
  }

  try {
    $uri = [Uri]::new($trimmed)
  }
  catch {
    return $trimmed
  }

  $builder = [System.UriBuilder]::new($uri)
  $builder.Path = ''
  $builder.Query = ''
  $builder.Fragment = ''

  if ($PreferLoopback) {
    if ($builder.Host -ieq 'localhost' -or $builder.Host -ieq 'gateway' -or $builder.Host -ieq 'host.docker.internal' -or $builder.Host -eq '::1' -or $builder.Host -eq '0.0.0.0') {
      $builder.Host = '127.0.0.1'
    }
  }
  elseif ($builder.Host -eq '::1') {
    $builder.Host = 'localhost'
  }

  return $builder.Uri.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/')
}

function Resolve-TansuBaseUrls {
  [CmdletBinding()]
  param(
    [switch]$PreferLoopbackForGateway
  )

  Import-TansuDotEnv | Out-Null

  $runningInContainer = [string]::Equals(
    [Environment]::GetEnvironmentVariable('DOTNET_RUNNING_IN_CONTAINER', [EnvironmentVariableTarget]::Process),
    'true',
    [StringComparison]::OrdinalIgnoreCase
  )

  $publicRaw = [Environment]::GetEnvironmentVariable('PUBLIC_BASE_URL', [EnvironmentVariableTarget]::Process)
  if ([string]::IsNullOrWhiteSpace($publicRaw)) {
    $publicRaw = 'http://127.0.0.1:8080'
  }

  $publicNormalized = Normalize-TansuBaseUrl -Url $publicRaw -PreferLoopback:(-not $runningInContainer)

  $gatewayRaw = [Environment]::GetEnvironmentVariable('GATEWAY_BASE_URL', [EnvironmentVariableTarget]::Process)
  if ([string]::IsNullOrWhiteSpace($gatewayRaw)) {
    if ($runningInContainer) {
      $gatewayRaw = 'http://gateway:8080'
    }
    else {
      $gatewayRaw = $publicNormalized
    }
  }

  $preferGatewayLoopback = (-not $runningInContainer) -or $PreferLoopbackForGateway.IsPresent
  $gatewayNormalized = Normalize-TansuBaseUrl -Url $gatewayRaw -PreferLoopback:$preferGatewayLoopback

  if ((-not $runningInContainer) -and $gatewayNormalized -eq 'http://gateway:8080') {
    $gatewayNormalized = $publicNormalized
  }

  [pscustomobject]@{
    PublicBaseUrl  = $publicNormalized
    GatewayBaseUrl = $gatewayNormalized
  }
}
