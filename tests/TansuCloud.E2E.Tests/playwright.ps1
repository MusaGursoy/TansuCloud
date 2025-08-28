param()
# Ensure Playwright browsers are installed
try {
  Write-Host "Installing Playwright browsers..."
  dotnet tool restore | Out-Null
  npx --version | Out-Null 2>$null
} catch {
  # ignore missing npx, we don't need it
}
try {
  pwsh -NoProfile -c "dotnet tool run playwright install --with-deps chromium" | Out-Null
} catch {
  dotnet tool run playwright install chromium | Out-Null
}
Write-Host "Playwright install complete"
