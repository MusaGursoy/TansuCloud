$ErrorActionPreference = 'Stop'

# Run readiness (soft gate)
& "$PSScriptRoot/sigNoz-ready.ps1" | Write-Host

# Run the specific test
& dotnet test "$PSScriptRoot/../../tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj" -c Debug --filter FullyQualifiedName~TansuCloud.E2E.Tests.SigNozExceptionCaptureE2E.Storage_Throw_Emits_ErrorSpan_And_ErrorLog
exit $LASTEXITCODE
