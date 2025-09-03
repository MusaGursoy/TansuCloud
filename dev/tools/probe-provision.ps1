param(
  [string]$Gateway = 'http://localhost:8080',
  [string]$ClientId = 'tansu-dashboard',
  [string]$ClientSecret = 'dev-secret',
  [string]$Scope = 'db.write admin.full'
)

$ErrorActionPreference = 'Stop'
$tok = Invoke-RestMethod -Uri ("$Gateway/identity/connect/token") -Method Post -ContentType 'application/x-www-form-urlencoded' -Body ("grant_type=client_credentials&client_id=$ClientId&client_secret=$ClientSecret&scope=" + [System.Net.WebUtility]::UrlEncode($Scope))
Write-Host ('Got token: ' + ($tok.access_token.Substring(0,20) + '...'))
$headers = @{ Authorization = ("Bearer " + $tok.access_token) }
$payload = @{ tenantId='acme-dev'; displayName='ACME Dev'; region='eu' } | ConvertTo-Json

try {
  $resp = Invoke-WebRequest -Uri 'http://localhost:5278/api/provisioning/tenants' -Method Post -Headers $headers -Body $payload -ContentType 'application/json' -SkipHttpErrorCheck
  Write-Host ("Status: {0}" -f [int]$resp.StatusCode)
  if ($resp.Headers.'WWW-Authenticate') { Write-Host ('WWW-Authenticate: ' + ($resp.Headers.'WWW-Authenticate' -join ', ')) }
  if ($resp.Content) { Write-Host $resp.Content }
} catch {
  Write-Host $_.Exception.Message -ForegroundColor Red
  exit 1
}
