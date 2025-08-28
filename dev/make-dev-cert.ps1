param(
    [string]$CertName = "tansu-gateway-dev",
    [SecureString]$Password,
    [string]$OutDir = (Join-Path (Split-Path -Path $PSScriptRoot -Parent) 'certs')
)

$ErrorActionPreference = 'Stop'

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$certPath = Join-Path $OutDir "gateway.pfx"

if (-not $Password) {
    Write-Host "Enter a password to protect the PFX file:" -ForegroundColor Yellow
    $Password = Read-Host -AsSecureString
}

Write-Host "Generating self-signed certificate for development..."

# Create a self-signed cert with SANs for localhost and 127.0.0.1
$cert = New-SelfSignedCertificate -DnsName "localhost","127.0.0.1" -FriendlyName $CertName -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2) -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256

# Export to PFX
Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $Password | Out-Null

Write-Host "PFX exported to: $certPath"
Write-Host "Remember to set environment variable GATEWAY_CERT_PASSWORD to the chosen password (not stored by this script)."
Write-Host "In docker-compose.yml, mount ./certs:/certs:ro to enable HTTPS at the gateway."
