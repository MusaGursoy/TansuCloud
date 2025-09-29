param(
    [string]$Tenant = 'e2e-test',
    [string]$BaseUrl
)
$ErrorActionPreference='Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null
$urls = Resolve-TansuBaseUrls

if (-not $PSBoundParameters.ContainsKey('BaseUrl') -or [string]::IsNullOrWhiteSpace($BaseUrl)) {
  $BaseUrl = $urls.PublicBaseUrl
}
$tokObj = & "$PSScriptRoot\get-token.ps1" | ConvertFrom-Json
$token = $tokObj.access_token
$headers = @{ 'Authorization' = "Bearer $token"; 'X-Tansu-Tenant' = $Tenant }
try {
  $res = Invoke-WebRequest -Uri "$BaseUrl/storage/api/buckets" -Headers $headers -Method Get
  Write-Host "Status: $($res.StatusCode)"; if ($res.Content) { Write-Host "Body:`n$($res.Content)" }
} catch {
  if ($_.Exception.Response) {
    $resp = [System.Net.Http.HttpResponseMessage]$_.Exception.Response
    Write-Host "Status: $($resp.StatusCode)"
    $body = $null
    try { if ($resp.Content) { $body = $resp.Content.ReadAsStringAsync().Result } } catch {}
    if ($body) { Write-Host "Body:`n$body" }
  } else { throw }
}