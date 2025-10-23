# EF Core Compiled Model - CI/CD Integration

## Overview

The EF Core compiled model provides ~20% faster DbContext initialization by pre-generating metadata at build time. This is **critical for Task 45 (Serverless Functions Service)** where frequent cold starts make every millisecond count.

## When to Use Compiled Model

### ✅ Use Compiled Model For

- **TansuCloud.Functions** (Task 45) - Frequent cold starts
- Serverless/FaaS workloads - High cold start frequency
- Azure Functions, AWS Lambda - Per-invocation startup cost

### ❌ Keep Runtime Model For

- **TansuCloud.Database** - Long-running service
- **TansuCloud.Gateway** - Long-running service
- **TansuCloud.Dashboard** - Long-running service
- **TansuCloud.Storage** - Long-running service
- **TansuCloud.Identity** - Long-running service
- **TansuCloud.Telemetry** - Long-running service

## CI/CD Pipeline Integration

### GitHub Actions Workflow

Add this step **before Docker build** in your CI/CD pipeline:

```yaml
name: Build and Deploy

on:
  push:
    branches: [ master, main ]
  pull_request:
    branches: [ master, main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '9.0.x'
    
    # ⚡ Generate EF Core Compiled Model
    - name: Generate EF Core Compiled Model
      run: |
        dotnet tool install --global dotnet-ef --version 9.0.0
        dotnet ef dbcontext optimize \
          --output-dir EF \
          --namespace TansuCloud.Database.EF \
          --context TansuDbContext \
          --project TansuCloud.Database/TansuCloud.Database.csproj \
          --startup-project TansuCloud.Database/TansuCloud.Database.csproj
    
    # Continue with normal build...
    - name: Build Docker images
      run: docker compose -f docker-compose.prod.yml build
```

### Local Development Script

Create `dev/tools/generate-compiled-model.ps1`:

```powershell
#!/usr/bin/env pwsh
# Generate EF Core compiled model for Functions service optimization

$ErrorActionPreference = 'Stop'

Write-Host "🔧 Generating EF Core compiled model..." -ForegroundColor Cyan

# Ensure dotnet-ef tool is installed
$efTool = dotnet tool list --global | Select-String "dotnet-ef"
if (-not $efTool) {
    Write-Host "Installing dotnet-ef tool..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-ef --version 9.0.0
}

# Generate compiled model
Push-Location $PSScriptRoot/../..
try {
    dotnet ef dbcontext optimize `
        --output-dir EF `
        --namespace TansuCloud.Database.EF `
        --context TansuDbContext `
        --project TansuCloud.Database/TansuCloud.Database.csproj `
        --startup-project TansuCloud.Database/TansuCloud.Database.csproj `
        --verbose
    
    Write-Host "✅ Compiled model generated successfully!" -ForegroundColor Green
    Write-Host "   Location: TansuCloud.Database/EF/" -ForegroundColor Gray
    Write-Host "   Namespace: TansuCloud.Database.EF" -ForegroundColor Gray
} catch {
    Write-Host "❌ Failed to generate compiled model: $_" -ForegroundColor Red
    exit 1
} finally {
    Pop-Location
}
```

### Docker Build Integration

Update your `Dockerfile` for Functions service:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["TansuCloud.Database/TansuCloud.Database.csproj", "TansuCloud.Database/"]
COPY ["TansuCloud.Functions/TansuCloud.Functions.csproj", "TansuCloud.Functions/"]
RUN dotnet restore "TansuCloud.Functions/TansuCloud.Functions.csproj"

# Copy source code
COPY . .

# ⚡ Generate EF Core compiled model (critical for cold start performance)
RUN dotnet tool install --global dotnet-ef --version 9.0.0 && \
    export PATH="$PATH:/root/.dotnet/tools" && \
    dotnet ef dbcontext optimize \
      --output-dir EF \
      --namespace TansuCloud.Database.EF \
      --context TansuDbContext \
      --project TansuCloud.Database/TansuCloud.Database.csproj \
      --startup-project TansuCloud.Database/TansuCloud.Database.csproj

# Build application
WORKDIR "/src/TansuCloud.Functions"
RUN dotnet build "TansuCloud.Functions.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TansuCloud.Functions.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TansuCloud.Functions.dll"]
```

## Regeneration Triggers

**MUST regenerate compiled model when:**

- ✅ Any changes to `TansuDbContext.cs`
- ✅ Adding/removing entity classes
- ✅ Modifying entity configurations (Fluent API)
- ✅ Adding/removing properties to entities
- ✅ Changing ValueComparers or ValueConverters

**NO need to regenerate when:**

- ❌ Changing business logic in services
- ❌ Updating controllers or APIs
- ❌ Modifying appsettings.json
- ❌ Adding new migrations (migrations are separate)

## Verification

After generating the compiled model, verify it works:

```bash
# Check generated files
ls TansuCloud.Database/EF/

# Expected output:
# TansuDbContextModel.cs
# DocumentEntityType.cs
# OutboxEventEntityType.cs
# ... (other entity types)

# Build and run tests
dotnet build TansuCloud.Database/TansuCloud.Database.csproj
dotnet test tests/TansuCloud.Database.UnitTests/
```

## Performance Benchmarks

### Standard Services (Long-running)

```
Application Startup:
├─ Runtime Model: ~500ms (once per deployment)
├─ Compiled Model: ~400ms (once per deployment)
└─ Savings: ~100ms per restart (negligible)

Per HTTP Request:
├─ Both: <1ms (model cached in memory)
└─ Savings: 0ms (no difference)
```

### Functions Service (Serverless)

```
Per Cold Start:
├─ Runtime Model: ~500ms
├─ Compiled Model: ~400ms
└─ Savings: ~100ms per cold start (significant)

Impact at Scale:
├─ 1,000 invocations/day: saves 1 min/day
├─ 10,000 invocations/day: saves 10 min/day
└─ 100,000 invocations/day: saves 3 hours/day
```

## Troubleshooting

### Issue: "Could not find compiled model"

**Solution**: Ensure the EF directory exists and `TryUseCompiledModel()` is uncommented in the Functions service.

### Issue: "Version mismatch"

**Solution**: Regenerate the compiled model with the same EF Core version as your project.

### Issue: "Type not found"

**Solution**: Ensure the namespace `TansuCloud.Database.EF` matches in both generation and usage.

### Issue: "Reflection error"

**Solution**: Check that `TansuDbContextModel.Instance` property exists and is public static.

## Best Practices

1. **Automate Generation**: Always generate in CI/CD, never commit generated files to git
2. **Version Control**: Add `TansuCloud.Database/EF/` to `.gitignore`
3. **Service-Specific**: Only enable for services that need it (Functions)
4. **Fallback**: Always implement graceful fallback to runtime model
5. **Testing**: Run full test suite after regeneration

## References

- EF Core Documentation: <https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#compiled-models>
- TansuCloud Implementation: `TansuCloud.Database/Services/TenantDbContextFactory.cs`
- Task 45 Specification: `Tasks-M5.md` (Performance Optimization Decision section)
