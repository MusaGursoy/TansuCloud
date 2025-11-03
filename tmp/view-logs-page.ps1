# Quick script to view the Logs page in Playwright browser
using namespace Microsoft.Playwright

$playwright = [Playwright]::CreateAsync().GetAwaiter().GetResult()
$browser = $playwright.Chromium.LaunchAsync(@{Headless=$false}).GetAwaiter().GetResult()
$page = $browser.NewPageAsync().GetAwaiter().GetResult()

Write-Host "Opening Logs page at http://127.0.0.1:8080/dashboard/admin/observability/logs ..."
$page.GotoAsync('http://127.0.0.1:8080/dashboard/admin/observability/logs', @{Timeout=60000}).GetAwaiter().GetResult()

Write-Host ""
Write-Host "Browser opened! You can interact with the page."
Write-Host "Press Enter to close the browser..."
Read-Host

$browser.CloseAsync().GetAwaiter().GetResult()
$playwright.Dispose()
Write-Host "Browser closed."
