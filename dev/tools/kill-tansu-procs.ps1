$ErrorActionPreference = 'SilentlyContinue'
# Stop background jobs
$jobs = Get-Job | Where-Object { $_.Name -like 'tansu-*' }
if ($jobs) { $jobs | Stop-Job -Force; $jobs | Remove-Job }
# Kill stray processes
$procs = 'TansuCloud.Identity','TansuCloud.Database','TansuCloud.Storage','TansuCloud.Dashboard','TansuCloud.Gateway'
foreach ($p in $procs) {
  Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force
}
Write-Host 'Stopped jobs and killed TansuCloud processes.'
