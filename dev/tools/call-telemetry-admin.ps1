#requires -Version 7.0
param(
    [ValidateSet('Get','Post','Put','Delete','Patch','Head','Options')]
    [string]$Method = 'Get',
    [string]$Path = '/api/admin/envelopes',
    [string]$BaseUrl,
    [Parameter(ValueFromPipeline = $true)]
    [object]$Body,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/common.ps1"
Import-TansuDotEnv | Out-Null

$apiKey = [Environment]::GetEnvironmentVariable('TELEMETRY__ADMIN__APIKEY', [EnvironmentVariableTarget]::Process)
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'TELEMETRY__ADMIN__APIKEY is not configured. Update .env or your secrets store.'
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = [Environment]::GetEnvironmentVariable('TELEMETRY__DIRECT__BASEURL', [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        $BaseUrl = 'http://127.0.0.1:5279'
    }
}

$normalizedBase = Normalize-TansuBaseUrl -Url $BaseUrl -PreferLoopback

if (-not $Path.StartsWith('/')) {
    $Path = '/' + $Path
}

$uri = $normalizedBase.TrimEnd('/') + $Path

Write-Host ("Invoking {0} {1}" -f $Method.ToUpperInvariant(), $uri) -ForegroundColor Cyan

$headers = @{ Authorization = "Bearer $apiKey" }

$invokeParams = @{
    Method            = $Method
    Uri               = $uri
    Headers           = $headers
    SkipHttpErrorCheck = $true
    UseBasicParsing   = $true
}

if ($PSBoundParameters.ContainsKey('Body')) {
    if ($Body -is [string]) {
        $invokeParams['Body'] = $Body
        $invokeParams['ContentType'] = 'application/json'
    } elseif ($Body -is [System.Collections.IDictionary] -or $Body -is [hashtable]) {
        $invokeParams['Body'] = ($Body | ConvertTo-Json -Depth 10)
        $invokeParams['ContentType'] = 'application/json'
    } else {
        $invokeParams['Body'] = ($Body | ConvertTo-Json -Depth 10)
        $invokeParams['ContentType'] = 'application/json'
    }
}

try {
    $response = Invoke-WebRequest @invokeParams
} catch {
    Write-Error ("Request failed: {0}" -f $_.Exception.Message)
    exit 1
}

$statusCode = [int]$response.StatusCode
Write-Host ("Status: {0}" -f $statusCode) -ForegroundColor Yellow

if ($Raw) {
    $response.Content
    return
}

if ($response.Content) {
    try {
        $json = $response.Content | ConvertFrom-Json -ErrorAction Stop
        $json
    } catch {
        $response.Content
    }
}
