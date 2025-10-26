$ErrorActionPreference = 'Stop'

$base = 'http://127.0.0.1:8080'

$tokenResp = Invoke-RestMethod -Method Post -Uri "$base/identity/connect/token" -Body 'grant_type=client_credentials&client_id=tansu-dashboard&client_secret=dev-secret&scope=admin.full' -ContentType 'application/x-www-form-urlencoded'
$token = $tokenResp.access_token

Invoke-RestMethod -Method Post -Uri "$base/db/api/provisioning/tenants" -Headers @{ 'X-Provision-Key' = 'letmein' } -ContentType 'application/json' -Body '{"tenantId":"manual-jsonpatch","displayName":"Manual"}' | Out-Null

$collection = Invoke-RestMethod -Method Post -Uri "$base/db/api/collections" -Headers @{ 'Authorization' = "Bearer $token"; 'X-Tansu-Tenant' = 'manual-jsonpatch' } -ContentType 'application/json' -Body '{"name":"manual"}'

$documentBody = @{ collectionId = $collection.id; content = @{ title = 'Test'; tags = @('tag1') } } | ConvertTo-Json -Depth 5
$document = Invoke-RestMethod -Method Post -Uri "$base/db/api/documents" -Headers @{ 'Authorization' = "Bearer $token"; 'X-Tansu-Tenant' = 'manual-jsonpatch' } -ContentType 'application/json' -Body $documentBody
$docId = $document.id

$patchBody = '[{"op":"add","path":"/content/tags/-","value":"tag2"}]'
$response = Invoke-WebRequest -Method Patch -Uri "$base/db/api/documents/$docId" -Headers @{ 'Authorization' = "Bearer $token"; 'X-Tansu-Tenant' = 'manual-jsonpatch'; 'Content-Type' = 'application/json-patch+json' } -Body $patchBody

Write-Host "Status: $($response.StatusCode)"
Write-Host "ContentLength: $($response.RawContentLength)"
Write-Host "Body:`n$($response.Content)"
