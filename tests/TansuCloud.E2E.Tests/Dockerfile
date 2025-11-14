# Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

# E2E Tests Dockerfile - Containerized Testing
# Purpose: Run E2E tests inside Docker network to access internal services
# Usage: docker compose --profile testing up --build e2e-tests

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Copy solution and all project files for restore
COPY TansuCloud.sln ./
COPY TansuCloud.Gateway/*.csproj ./TansuCloud.Gateway/
COPY TansuCloud.Identity/*.csproj ./TansuCloud.Identity/
COPY TansuCloud.Identity.Data/*.csproj ./TansuCloud.Identity.Data/
COPY TansuCloud.Dashboard/*.csproj ./TansuCloud.Dashboard/
COPY TansuCloud.Database/*.csproj ./TansuCloud.Database/
COPY TansuCloud.Storage/*.csproj ./TansuCloud.Storage/
COPY TansuCloud.Telemetry/*.csproj ./TansuCloud.Telemetry/
COPY TansuCloud.Observability.Shared/*.csproj ./TansuCloud.Observability.Shared/
COPY TansuCloud.Audit/*.csproj ./TansuCloud.Audit/
COPY TansuCloud.Telemetry.Contracts/*.csproj ./TansuCloud.Telemetry.Contracts/
COPY tests/TansuCloud.E2E.Tests/*.csproj ./tests/TansuCloud.E2E.Tests/
COPY tests/TansuCloud.Dashboard.UnitTests/*.csproj ./tests/TansuCloud.Dashboard.UnitTests/

# Restore all dependencies
RUN dotnet restore tests/TansuCloud.E2E.Tests/TansuCloud.E2E.Tests.csproj

# Copy all source code
COPY . .

# Build test project
WORKDIR /src/tests/TansuCloud.E2E.Tests
RUN dotnet build TansuCloud.E2E.Tests.csproj -c Release --no-restore

# Install Playwright browsers (required for Dashboard tests)
RUN pwsh -Command "cd /root/.microsoft/playwright; if (Test-Path './ms-playwright') { exit 0 }; dotnet tool install --global Microsoft.Playwright.CLI; /root/.dotnet/tools/playwright install --with-deps chromium"

# Set environment variable to indicate running in container
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Default: run all E2E tests
ENTRYPOINT ["dotnet", "test", "TansuCloud.E2E.Tests.csproj", "-c", "Release", "--no-build", "--logger:console;verbosity=normal"]

# Examples:
# Run specific test: docker compose run --rm e2e-tests --filter "FullyQualifiedName~LokiLogsE2E"
# Run all tests: docker compose run --rm e2e-tests
