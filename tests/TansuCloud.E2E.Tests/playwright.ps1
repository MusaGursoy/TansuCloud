param(
  [string]$Browser = "chromium"
)

# Idempotent Playwright browser installer with skip and cache support
if ($env:PLAYWRIGHT_SKIP_INSTALL -eq '1') {
  Write-Host "PLAYWRIGHT_SKIP_INSTALL=1 → skipping Playwright install"
  return
}

# Detect existing installation to avoid re-downloading on every run
$LocalBrowsers = Join-Path $env:USERPROFILE ".ms-playwright"
if (-not (Test-Path $LocalBrowsers)) { $LocalBrowsers = "$PSScriptRoot\..\..\..\.playwright" }
if (-not (Test-Path $LocalBrowsers)) { $LocalBrowsers = "$PSScriptRoot\..\..\.playwright" }

function Test-ChromiumInstalled {
  param([string]$Root)
  if (-not (Test-Path $Root)) { return $false }
  $chromium = Get-ChildItem -Path $Root -Recurse -Depth 2 -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'chromium' } |
    Select-Object -First 1
  return $null -ne $chromium
}

if (Test-ChromiumInstalled -Root $LocalBrowsers) {
  Write-Host "Playwright browsers already present under $LocalBrowsers → skip install"
  return
}

Write-Host "Installing Playwright browsers to cache..."
try {
  dotnet tool restore | Out-Null
} catch {}

# Prefer the Playwright .NET tool; --with-deps is Linux-only and slow; omit on Windows
try {
  dotnet tool run playwright install $Browser | Out-Null
} catch {
  # Fallback to Node tool if present
  try {
    npx --yes playwright install $Browser | Out-Null
  } catch {
    Write-Warning "Playwright install failed: $_"
    throw
  }
}
Write-Host "Playwright install complete (browser=$Browser)"
