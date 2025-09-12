param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$Tenant = 'acme-dev',
    [string]$ClientId = 'tansu-dashboard',
    [string]$ClientSecret = 'dev-secret',
    [string]$Scope = 'storage.read'
)

$ErrorActionPreference = 'Stop'

Write-Host "BaseUrl=$BaseUrl Tenant=$Tenant Scope=$Scope" -ForegroundColor Cyan

function Decode-JwtPayload([string]$jwt) {
    if ([string]::IsNullOrWhiteSpace($jwt) -or -not $jwt.Contains('.')) { return $null }
    try {
        $parts = $jwt.Split('.')
        $p = $parts[1]
        # base64url -> base64
        $p = $p.Replace('-', '+').Replace('_', '/')
        $pad = 4 - ($p.Length % 4); if ($pad -lt 4) { $p += '=' * $pad }
        $bytes = [Convert]::FromBase64String($p)
        return [Text.Encoding]::UTF8.GetString($bytes)
    } catch { return $null }
}

function Wait-Healthy([string]$url, [int]$timeoutSec = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -SkipHttpErrorCheck
            if ([int]$r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 1
    }
    return $false
}

# Wait for Identity and Storage readiness via gateway (try both canonical and prefixed)
if (-not (Wait-Healthy "$BaseUrl/identity/health/ready" 20)) {
    if (-not (Wait-Healthy "$BaseUrl/health/ready" 20)) {
        Write-Error "Identity not ready at $BaseUrl/identity/health/ready (and root /health/ready)"
        exit 1
    }
}
if (-not (Wait-Healthy "$BaseUrl/storage/health/ready" 40)) {
    Write-Error "Storage not ready at $BaseUrl/storage/health/ready"
    exit 1
}

# 1) Acquire token via client_credentials (try gateway root token endpoint first, then prefixed, then direct Identity)
$gatewayTokenEndpointRoot = "$BaseUrl/connect/token"
$gatewayTokenEndpointPrefixed = "$BaseUrl/identity/connect/token"
$directIdentityBase = 'http://localhost:5095'
$directTokenEndpoint = "$directIdentityBase/connect/token"

# Build x-www-form-urlencoded body (grant_type + scope only) and use client_secret_basic for auth
$kvPairs = @(
    'grant_type=client_credentials',
    'scope=' + [uri]::EscapeDataString($Scope)
)
$bodyString = ($kvPairs -join '&')

# Prepare Basic auth header
$pair = "${ClientId}:${ClientSecret}"
$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
$tokenHeaders = @{ Accept = 'application/json'; Authorization = "Basic $basic" }

function Request-Token([string]$endpoint) {
    try {
        $resp = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $tokenHeaders -ContentType 'application/x-www-form-urlencoded' -Body $bodyString -SkipHttpErrorCheck
        if ($null -eq $resp) { return $null }
        if ($resp.PSObject.Properties.Name -contains 'access_token') { return $resp }
        # Not a JSON object with access_token; log and return null
        try { Write-Host ("Token JSON (no access_token): " + ($resp | ConvertTo-Json -Depth 5)) -ForegroundColor DarkYellow } catch { Write-Host ($resp | Out-String) }
        return $null
    } catch {
        # For non-success, Invoke-RestMethod throws unless SkipHttpErrorCheck handled by newer PowerShell
        Write-Host "Token request to $endpoint failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
        return $null
    }
}

# Try root token endpoint first (per discovery), then prefixed, then direct
$tokenRes = Request-Token $gatewayTokenEndpointRoot
if ($null -eq $tokenRes -or [string]::IsNullOrWhiteSpace($tokenRes.access_token)) {
    Write-Host "Retrying token request at prefixed endpoint..." -ForegroundColor Yellow
    $tokenRes = Request-Token $gatewayTokenEndpointPrefixed
}
if ($null -eq $tokenRes -or [string]::IsNullOrWhiteSpace($tokenRes.access_token)) {
    Write-Host "Retrying token request directly to Identity..." -ForegroundColor Yellow
    $tokenRes = Request-Token $directTokenEndpoint
}

$accessToken = $null
try {
    $accessToken = ($tokenRes | Select-Object -ExpandProperty access_token -ErrorAction Stop)
} catch {
    $accessToken = $tokenRes.access_token
}
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    if ($null -ne $tokenRes) {
        try { Write-Host ("Token JSON: " + ($tokenRes | ConvertTo-Json -Depth 5)) -ForegroundColor DarkYellow } catch { }
    }
    Write-Error "Token acquisition failed: no access_token in response"
    exit 1
}

# Debug: print token payload (for troubleshooting audiences/scopes)
$payload = Decode-JwtPayload $accessToken
if ($payload) { Write-Host "TOKEN_PAYLOAD=$payload" -ForegroundColor DarkGray }

# 2) Call Storage buckets endpoint via Gateway
$headers = @{
    Authorization    = "Bearer $accessToken"
    'X-Tansu-Tenant' = $Tenant
}
$storageUrl = "$BaseUrl/storage/api/buckets"

try {
    $res = Invoke-WebRequest -Method Get -Uri $storageUrl -Headers $headers -SkipHttpErrorCheck
} catch {
    Write-Error "Buckets request failed: $($_.Exception.Message)"
    exit 1
}

$status = [int]$res.StatusCode
Write-Host "STATUS=$status" -ForegroundColor Yellow

if ($status -eq 200) {
    $bodyTxt = $res.Content
    if ($null -ne $bodyTxt -and $bodyTxt.Length -gt 2000) {
        $bodyTxt = $bodyTxt.Substring(0, 2000) + '...'
    }
    Write-Host "BODY=$bodyTxt" -ForegroundColor Green
} else {
    Write-Host "Response headers:" -ForegroundColor DarkCyan
    $res.Headers.GetEnumerator() | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Key, ($_.Value -join ', ')) }
    if ($res.Content) { Write-Host "BODY=$($res.Content)" }
}
