param(
    [string]$Tenant = "e2e-$(($env:COMPUTERNAME ?? $env:HOSTNAME) -as [string]).ToLower()",
    [string]$Bucket = "e2e-cli-$(New-Guid | ForEach-Object { $_.Guid.Replace('-', '') })",
    [string]$BaseUrl
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Import-TansuDotEnv | Out-Null
$urls = Resolve-TansuBaseUrls

if (-not $PSBoundParameters.ContainsKey('BaseUrl') -or [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = $urls.PublicBaseUrl
}

# Get token
$tokObj = & "$PSScriptRoot\get-token.ps1" | ConvertFrom-Json
$token = $tokObj.access_token

$createUrl = "$BaseUrl/storage/api/buckets/$Bucket"
Write-Host "Creating bucket $Bucket for tenant $Tenant via $createUrl"

try {
    $res = Invoke-WebRequest -Method Put -Uri $createUrl -Headers @{ 'Authorization' = "Bearer $token"; 'X-Tansu-Tenant' = $Tenant }
    Write-Host "Status: $($res.StatusCode)"
    Write-Host "Headers:`n$($res.Headers | Out-String)"
    if ($res.Content) { Write-Host "Body:`n$($res.Content)" }
} catch {
    if ($_.Exception.Response) {
        $resp = [System.Net.Http.HttpResponseMessage]$_.Exception.Response
        Write-Host "Status: $($resp.StatusCode)"
        Write-Host "Headers:`n$($resp.Headers | Out-String)"
        $body = $null
        try {
            if ($resp.Content) { $body = $resp.Content.ReadAsStringAsync().Result }
        } catch { }
        if ($body) { Write-Host "Body:`n$body" }
    } else {
        throw
    }
}