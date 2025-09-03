<#
PowerShell dev script: verify Identity discovery, get a token via gateway (client_credentials),
then provision a tenant twice via Database (idempotency check).
#>
param(
  [string]$Gateway = 'http://localhost:8080',
  [string]$ClientId = 'tansu-dashboard',
  [string]$ClientSecret = 'dev-secret',
  [string]$Scope = 'db.write admin.full',
  [string]$TenantId = 'acme-dev',
  [string]$TenantName = 'ACME Dev',
  [string]$Region = 'eu'
)

$ErrorActionPreference = 'Stop'

function Show-Header($label){
  Write-Host '============================================================'
  Write-Host $label
  Write-Host '============================================================'
}

function Show-Resp($label, $resp){
  Write-Host "[$label]"
  try { $resp | ConvertTo-Json -Depth 8 | Write-Host } catch { Write-Host ($resp | Out-String) }
}

function Base64UrlDecode([string]$s){
  $padded = $s.Replace('-', '+').Replace('_', '/')
  switch ($padded.Length % 4) {
    2 { $padded += '==' }
    3 { $padded += '=' }
  }
  return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($padded))
}

try {
  # 1) Discovery via gateway
  Show-Header 'OIDC Discovery (gateway)'
  $disc = Invoke-RestMethod -Uri "$Gateway/identity/.well-known/openid-configuration" -TimeoutSec 15
  Show-Resp 'discovery' $disc

  # 2) Token via gateway (client_credentials)
  Show-Header 'Token (client_credentials via gateway)'
  $scopeEnc = [System.Net.WebUtility]::UrlEncode($Scope)
  $body = "grant_type=client_credentials&client_id=$ClientId&client_secret=$ClientSecret&scope=$scopeEnc"
  $tok = Invoke-RestMethod -Uri "$Gateway/identity/connect/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body $body -TimeoutSec 30
  Show-Resp 'token' $tok

  if (-not $tok.access_token) { throw 'No access_token returned' }

  # Dump decoded JWT header/payload for quick sanity (no signature verification)
  $parts = $tok.access_token -split '\.'
  if ($parts.Length -ge 2) {
    $hdr = Base64UrlDecode $parts[0] | ConvertFrom-Json
    $pld = Base64UrlDecode $parts[1] | ConvertFrom-Json
    Show-Header 'Decoded JWT (header)'
    $hdr | ConvertTo-Json -Depth 5 | Write-Host
    Show-Header 'Decoded JWT (payload)'
    $pld | ConvertTo-Json -Depth 5 | Write-Host
  }

  # 3) Provision via gateway with Bearer token
  $headers = @{ 'Authorization' = "Bearer $($tok.access_token)"; 'Content-Type' = 'application/json' }
  $payload = @{ tenantId = $TenantId; displayName = $TenantName; region = $Region } | ConvertTo-Json

  Show-Header 'Provision #1'
  try {
    $prov1 = Invoke-RestMethod -Uri "$Gateway/db/api/provisioning/tenants" -Method Post -Headers $headers -Body $payload -TimeoutSec 30
    Show-Resp 'provision1' $prov1
  } catch {
    Write-Host "Provision1 failed: $($_.Exception.Message)" -ForegroundColor Yellow
    if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
      $resp = $_.Exception.Response
      Write-Host ("Status: {0} {1}" -f [int]$resp.StatusCode, $resp.ReasonPhrase)
      $wa = $resp.Headers.'WWW-Authenticate'
      if ($wa) { Write-Host ("WWW-Authenticate: {0}" -f ($wa -join ', ')) }
      try { $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); if ($content) { Write-Host $content } } catch {}
    }
    # Fallback: try direct Database endpoint to isolate
    try {
      Write-Host 'DIAG: Trying direct Database provisioning at http://localhost:5278/api/provisioning/tenants'
      $prov1Direct = Invoke-RestMethod -Uri 'http://localhost:5278/api/provisioning/tenants' -Method Post -Headers $headers -Body $payload -TimeoutSec 30
      Show-Resp 'provision1-direct' $prov1Direct
    } catch {
      Write-Host "Direct provisioning also failed: $($_.Exception.Message)" -ForegroundColor Yellow
      if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
        $resp2 = $_.Exception.Response
        Write-Host ("Direct Status: {0} {1}" -f [int]$resp2.StatusCode, $resp2.ReasonPhrase)
        $wa2 = $resp2.Headers.'WWW-Authenticate'
        if ($wa2) { Write-Host ("Direct WWW-Authenticate: {0}" -f ($wa2 -join ', ')) }
        try { $content2 = $resp2.Content.ReadAsStringAsync().GetAwaiter().GetResult(); if ($content2) { Write-Host $content2 } } catch {}
      }
    }
    # don't rethrow; continue to next steps
  }

  Show-Header 'Provision #2 (idempotency)'
  try {
    $prov2 = Invoke-RestMethod -Uri "$Gateway/db/api/provisioning/tenants" -Method Post -Headers $headers -Body $payload -TimeoutSec 30
    Show-Resp 'provision2' $prov2
  } catch {
    Write-Host "Provision2 failed: $($_.Exception.Message)" -ForegroundColor Yellow
    if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
      $resp = $_.Exception.Response
      Write-Host ("Status: {0} {1}" -f [int]$resp.StatusCode, $resp.ReasonPhrase)
      $wa = $resp.Headers.'WWW-Authenticate'
      if ($wa) { Write-Host ("WWW-Authenticate: {0}" -f ($wa -join ', ')) }
      $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      if ($content) { Write-Host $content }
    }
    throw
  }

  Write-Host 'DONE' -ForegroundColor Green
} catch {
  Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
  if ($_.Exception.Response -and ($_.Exception.Response.GetResponseStream())) {
    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $reader.ReadToEnd() | Write-Host
  }
  exit 1
}
