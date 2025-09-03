param(
  [string]$Gateway = 'http://localhost:8080',
  [string]$Identity = 'http://localhost:5095',
  [string]$DbUrl = 'http://localhost:5278',
  [string]$ClientId = 'tansu-dashboard',
  [string]$ClientSecret = 'dev-secret',
  [string]$Scope = 'db.write admin.full',
  [string]$TenantId = 'acme-dev',
  [string]$TenantName = 'ACME Dev',
  [string]$Region = 'eu'
)

$ErrorActionPreference = 'Stop'

Write-Host '== token via gateway ==' 
$scopeEnc = [System.Net.WebUtility]::UrlEncode($Scope)
$body = "grant_type=client_credentials&client_id=$ClientId&client_secret=$ClientSecret&scope=$scopeEnc"
$tok = $null
try {
  $tok = Invoke-RestMethod -Uri "$Gateway/identity/connect/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body $body -TimeoutSec 30
} catch {
  Write-Host ("Gateway token failed: {0}" -f $_.Exception.Message)
  Write-Host '== fallback: token via identity direct =='
  try {
    $tok = Invoke-RestMethod -Uri "$Identity/connect/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body $body -TimeoutSec 30
  } catch {
    throw
  }
}

# Debug token format
try {
  $t = $tok.access_token
  $segs = ($t -split '\.').Length
  Write-Host ("token len={0} segs={1}" -f ($t.Length), $segs)
  if ($segs -ge 1) {
    $h = ($t -split '\.')[0]
    function Decode-Base64Url([string]$s) {
      $p = $s.Replace('-', '+').Replace('_', '/')
      switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } 0 { } default { } }
      [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p))
    }
    try { $hd = Decode-Base64Url $h; Write-Host ("hdr={0}" -f $hd) } catch { Write-Host ("hdr-decode-error: {0}" -f $_.Exception.Message) }
  }
} catch {}
$headers = @{ 'Authorization' = "Bearer $($tok.access_token)"; 'Content-Type' = 'application/json' }
$payload = @{ tenantId = $TenantId; displayName = $TenantName; region = $Region } | ConvertTo-Json

Write-Host '== POST direct to DB, print status, WWW-Authenticate and body ==' 
$resp = Invoke-WebRequest -Uri "$DbUrl/api/provisioning/tenants" -Method Post -Headers $headers -Body $payload -UseBasicParsing -TimeoutSec 30 -SkipHttpErrorCheck
if ($resp -is [System.Net.Http.HttpResponseMessage]) {
  # PowerShell may return HttpResponseMessage when SkipHttpErrorCheck is used
  $status = [int]$resp.StatusCode
  Write-Host ("Status: {0}" -f $status)
  Write-Host 'Headers:'
  foreach ($h in $resp.Headers.GetEnumerator()) { Write-Host ("{0}: {1}" -f $h.Key, ($h.Value -join ', ')) }
  $wa = $resp.Headers.WwwAuthenticate
  if ($wa) { Write-Host ('WWW-Authenticate: {0}' -f ($wa -join ', ')) }
  if ($resp.Content) {
    try { $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch {}
    if ($content) { Write-Host 'Body:'; Write-Host $content }
  }
} else {
  # WebResponseObject
  Write-Host ('Status: {0}' -f $resp.StatusCode)
  Write-Host 'Headers:'
  $resp.Headers.GetEnumerator() | ForEach-Object { Write-Host ("{0}: {1}" -f $_.Key, ($_.Value -join ', ')) }
  $wa = $resp.Headers['WWW-Authenticate']
  if ($wa) { Write-Host ('WWW-Authenticate: {0}' -f ($wa -join ', ')) }
  if ($resp.Content) { Write-Host 'Body:'; Write-Host $resp.Content }
}

# Try again using HttpClient to ensure Authorization header formatting is correct
Write-Host '== POST via HttpClient (Authorization typed) =='
Add-Type -AssemblyName System.Net.Http
$hc = [System.Net.Http.HttpClient]::new()
$hc.Timeout = [TimeSpan]::FromSeconds(30)
$hc.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $tok.access_token)
$content = New-Object System.Net.Http.StringContent($payload, [System.Text.Encoding]::UTF8, 'application/json')
$hr = $hc.PostAsync("$DbUrl/api/provisioning/tenants", $content).GetAwaiter().GetResult()
Write-Host ("Status: {0}" -f [int]$hr.StatusCode)
foreach ($h in $hr.Headers) { Write-Host ("{0}: {1}" -f $h.Key, ($h.Value -join ', ')) }
if ($hr.Content) { $t = $hr.Content.ReadAsStringAsync().GetAwaiter().GetResult(); if ($t) { Write-Host 'Body:'; Write-Host $t } }
