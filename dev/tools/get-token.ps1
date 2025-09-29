param(
    [string]$ClientId = 'tansu-dashboard',
    [string]$ClientSecret,
    [string]$Scope = 'storage.write storage.read admin.full',
    [string]$TokenUrl
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null
$urls = Resolve-TansuBaseUrls

if (-not $PSBoundParameters.ContainsKey('ClientSecret') -or [string]::IsNullOrWhiteSpace($ClientSecret)) {
    $ClientSecret = [Environment]::GetEnvironmentVariable('DASHBOARD_CLIENT_SECRET', [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($ClientSecret)) {
        $ClientSecret = 'dev-secret'
    }
}

if (-not $PSBoundParameters.ContainsKey('TokenUrl') -or [string]::IsNullOrWhiteSpace($TokenUrl)) {
    $TokenUrl = "$($urls.PublicBaseUrl)/identity/connect/token"
}

Write-Host "Requesting token from $TokenUrl for client '$ClientId' with scope '$Scope'..."

# Use form-urlencoded body
$body = @{
    grant_type    = 'client_credentials'
    client_id     = $ClientId
    client_secret = $ClientSecret
    scope         = $Scope
}

$res = Invoke-RestMethod -Method Post -Uri $TokenUrl -ContentType 'application/x-www-form-urlencoded' -Body $body

if (-not $res -or -not $res.access_token) {
    throw 'No access_token in response. Raw response: ' + ($res | ConvertTo-Json -Depth 5)
}

$tok = $res.access_token
Write-Host ("ACCESS_TOKEN_LEN: " + $tok.Length)

# Decode JWT payload (base64url)
$parts = $tok.Split('.')
if ($parts.Length -lt 2) { throw 'Token is not a JWT' }
$mid = $parts[1]
$pad = (4 - ($mid.Length % 4)) % 4
$padded = $mid + ('=' * $pad)
$bytes = [Convert]::FromBase64String($padded.Replace('-', '+').Replace('_', '/'))
$payloadJson = [Text.Encoding]::UTF8.GetString($bytes)

Write-Host "JWT PAYLOAD:"
Write-Output $payloadJson

# Also output the full response as JSON at the end
$res | ConvertTo-Json -Depth 5
