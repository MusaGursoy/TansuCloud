#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate EF Core compiled model for TansuCloud.Functions service optimization.

.DESCRIPTION
    This script generates the EF Core compiled model that provides ~20% faster DbContext
    initialization. Critical for Task 45 (Serverless Functions Service) where frequent
    cold starts make every millisecond count.

.EXAMPLE
    .\generate-compiled-model.ps1
    
    Generates the compiled model in TansuCloud.Database/EF/ directory.

.NOTES
    Author: TansuCloud Team
    Date: 2025-10-20
    
    WHEN TO RUN:
    - Before implementing Task 45 (Serverless Functions)
    - After ANY changes to TansuDbContext.cs
    - After adding/removing entity classes
    - After modifying entity configurations
    
    WHERE IT'S USED:
    - TansuCloud.Functions service (Task 45) - ENABLED
    - Other services (Database, Gateway, etc.) - DISABLED (runtime model preferred)
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Colors for output
$cyan = [System.ConsoleColor]::Cyan
$green = [System.ConsoleColor]::Green
$yellow = [System.ConsoleColor]::Yellow
$red = [System.ConsoleColor]::Red
$gray = [System.ConsoleColor]::Gray

function Write-ColorHost {
    param(
        [Parameter(Mandatory)]
        [string]$Message,
        
        [Parameter()]
        [System.ConsoleColor]$Color = [System.ConsoleColor]::White
    )
    
    Write-Host $Message -ForegroundColor $Color
}

Write-ColorHost "🔧 EF Core Compiled Model Generator" -Color $cyan
Write-ColorHost "   For Task 45: Serverless Functions Service" -Color $gray
Write-ColorHost "" 

# Navigate to repository root
$scriptDir = $PSScriptRoot
$repoRoot = (Get-Item $scriptDir).Parent.Parent.FullName

Write-ColorHost "📂 Repository: $repoRoot" -Color $gray

Push-Location $repoRoot
try {
    # Check if dotnet-ef tool is installed
    Write-ColorHost "🔍 Checking for dotnet-ef tool..." -Color $cyan
    $efTool = dotnet tool list --global | Select-String "dotnet-ef"
    
    if (-not $efTool) {
        Write-ColorHost "   Installing dotnet-ef tool (v9.0.0)..." -Color $yellow
        dotnet tool install --global dotnet-ef --version 9.0.0
        
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install dotnet-ef tool"
        }
        
        Write-ColorHost "   ✅ dotnet-ef tool installed" -Color $green
    } else {
        Write-ColorHost "   ✅ dotnet-ef tool found" -Color $green
    }
    
    # Verify TansuCloud.Database project exists
    $dbProject = Join-Path $repoRoot "TansuCloud.Database\TansuCloud.Database.csproj"
    if (-not (Test-Path $dbProject)) {
        throw "TansuCloud.Database project not found at: $dbProject"
    }
    
    Write-ColorHost "   ✅ Database project found" -Color $green
    Write-ColorHost ""
    
    # Generate compiled model
    Write-ColorHost "⚡ Generating EF Core compiled model..." -Color $cyan
    Write-ColorHost "   This will create optimized metadata for faster DbContext initialization" -Color $gray
    Write-ColorHost ""
    
    $output = @()
    
    dotnet ef dbcontext optimize `
        --output-dir EF `
        --namespace TansuCloud.Database.EF `
        --context TansuDbContext `
        --project TansuCloud.Database\TansuCloud.Database.csproj `
        --startup-project TansuCloud.Database\TansuCloud.Database.csproj `
        --verbose 2>&1 | ForEach-Object {
            $output += $_
            Write-Host "   $_" -ForegroundColor $gray
        }
    
    if ($LASTEXITCODE -ne 0) {
        Write-ColorHost "" 
        Write-ColorHost "❌ Failed to generate compiled model" -Color $red
        Write-ColorHost ""
        Write-ColorHost "Common issues:" -Color $yellow
        Write-ColorHost "  1. DbContext has compilation errors" -Color $gray
        Write-ColorHost "  2. Missing project dependencies" -Color $gray
        Write-ColorHost "  3. EF Core version mismatch" -Color $gray
        Write-ColorHost ""
        Write-ColorHost "Full output:" -Color $yellow
        $output | ForEach-Object { Write-Host "  $_" -ForegroundColor $gray }
        exit 1
    }
    
    # Verify generated files
    $efDir = Join-Path $repoRoot "TansuCloud.Database\EF"
    if (-not (Test-Path $efDir)) {
        throw "EF directory not created. Generation may have failed silently."
    }
    
    $generatedFiles = Get-ChildItem $efDir -Filter "*.cs" -File
    if ($generatedFiles.Count -eq 0) {
        throw "No C# files generated in EF directory."
    }
    
    Write-ColorHost ""
    Write-ColorHost "✅ Compiled model generated successfully!" -Color $green
    Write-ColorHost ""
    Write-ColorHost "📊 Generated files ($($generatedFiles.Count) total):" -Color $cyan
    foreach ($file in $generatedFiles | Select-Object -First 10) {
        Write-ColorHost "   • $($file.Name)" -Color $gray
    }
    
    if ($generatedFiles.Count -gt 10) {
        Write-ColorHost "   ... and $($generatedFiles.Count - 10) more" -Color $gray
    }
    
    Write-ColorHost ""
    Write-ColorHost "📍 Location:" -Color $cyan
    Write-ColorHost "   $efDir" -Color $gray
    Write-ColorHost ""
    Write-ColorHost "🔧 Usage:" -Color $cyan
    Write-ColorHost "   The compiled model is DISABLED by default (runtime model used for flexibility)." -Color $gray
    Write-ColorHost "   To ENABLE for Task 45 (Functions service):" -Color $gray
    Write-ColorHost "   1. Uncomment code in TansuCloud.Database/Services/TenantDbContextFactory.cs" -Color $yellow
    Write-ColorHost "   2. Rebuild the Functions service" -Color $yellow
    Write-ColorHost ""
    Write-ColorHost "⚡ Performance impact:" -Color $cyan
    Write-ColorHost "   • Standard services: ~100ms saved at startup (once per deployment) - negligible" -Color $gray
    Write-ColorHost "   • Functions service: ~100ms saved per cold start - significant!" -Color $gray
    Write-ColorHost "   • At 1,000 invocations/day: saves ~1 minute/day" -Color $green
    Write-ColorHost "   • At 100,000 invocations/day: saves ~3 hours/day" -Color $green
    Write-ColorHost ""
    Write-ColorHost "⚠️  Remember to regenerate after:" -Color $yellow
    Write-ColorHost "   • Any changes to TansuDbContext.cs" -Color $gray
    Write-ColorHost "   • Adding/removing entity classes" -Color $gray
    Write-ColorHost "   • Modifying entity configurations" -Color $gray
    Write-ColorHost ""
    
} catch {
    Write-ColorHost ""
    Write-ColorHost "❌ Error: $($_.Exception.Message)" -Color $red
    Write-ColorHost ""
    Write-ColorHost "Stack trace:" -Color $gray
    Write-ColorHost $_.ScriptStackTrace -ForegroundColor $gray
    exit 1
} finally {
    Pop-Location
}
