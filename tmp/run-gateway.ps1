$root = Join-Path $PSScriptRoot '..'
Set-Location $root
$env:ASPNETCORE_URLS = 'http://127.0.0.1:9000'
$env:PUBLIC_BASE_URL = 'http://127.0.0.1:9000'
$env:GATEWAY_BASE_URL = 'http://127.0.0.1:9000'
$env:DOTNET_RUNNING_IN_CONTAINER = 'false'
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://127.0.0.1:4317'
$env:OTEL_EXPORTER_OTLP_PROTOCOL = 'grpc'
dotnet run --no-build --project .\TansuCloud.Gateway\TansuCloud.Gateway.csproj -c Debug
