param(
  [string]$Token,
  [string]$DbUrl = 'http://localhost:5278',
  [string]$TenantId = 'acme-dev',
  [string]$DisplayName = 'ACME Dev',
  [string]$Region = 'eu'
)

$ErrorActionPreference = 'Stop'
if (-not $Token) { throw 'Token is required' }
$headers = @{ Authorization = "Bearer $Token" }
$payload = @{ tenantId=$TenantId; displayName=$DisplayName; region=$Region } | ConvertTo-Json

try {
  $resp = Invoke-RestMethod -Uri ("$DbUrl/api/provisioning/tenants") -Method Post -Headers $headers -ContentType 'application/json' -Body $payload -TimeoutSec 120
  $resp | ConvertTo-Json -Depth 6 | Write-Host
} catch {
  if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
    $r = $_.Exception.Response
    Write-Host ("Status: {0} {1}" -f [int]$r.StatusCode, $r.ReasonPhrase)
    $wa = $r.Headers.'WWW-Authenticate'
    if ($wa) { Write-Host ("WWW-Authenticate: {0}" -f ($wa -join ', ')) }
  } else {
    Write-Host $_.Exception.Message
  }
  exit 1
}
