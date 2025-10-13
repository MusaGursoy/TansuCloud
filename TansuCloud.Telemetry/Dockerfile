# syntax=docker/dockerfile:1

# Build and publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG CACHE_BUST=2025-10-02-1
WORKDIR /src
COPY ["TansuCloud.Telemetry/TansuCloud.Telemetry.csproj", "TansuCloud.Telemetry/"]
COPY ["TansuCloud.Telemetry.Contracts/TansuCloud.Telemetry.Contracts.csproj", "TansuCloud.Telemetry.Contracts/"]
COPY ["TansuCloud.Observability.Shared/TansuCloud.Observability.Shared.csproj", "TansuCloud.Observability.Shared/"]
RUN dotnet restore "./TansuCloud.Telemetry/TansuCloud.Telemetry.csproj"
COPY . .
WORKDIR "/src/TansuCloud.Telemetry"
RUN dotnet publish "./TansuCloud.Telemetry.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final runtime image (chiseled, non-root by default)
FROM busybox:stable-musl AS bb
RUN mkdir -p /var/opt/tansu/telemetry && \
    chmod -R 777 /var/opt/tansu/telemetry

FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS final
ARG REPOSITORY_URL=https://github.com/MusaGursoy/TansuCloud
ARG BUILD_VERSION=0.0.0
ARG BUILD_COMMIT=unknown
ARG BUILD_DATE=unknown
LABEL org.opencontainers.image.source=$REPOSITORY_URL \
      org.opencontainers.image.url=$REPOSITORY_URL \
      org.opencontainers.image.title="TansuCloud.Telemetry" \
      org.opencontainers.image.description="Telemetry ingestion service for TansuCloud." \
      org.opencontainers.image.version=$BUILD_VERSION \
      org.opencontainers.image.revision=$BUILD_COMMIT \
      org.opencontainers.image.created=$BUILD_DATE

WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    TELEMETRY__DATABASE__FILEPATH=/var/opt/tansu/telemetry/telemetry.db
COPY --from=build /app/publish .
COPY --from=bb /bin/busybox /bin/busybox
COPY --from=bb --chown=app:app /var/opt/tansu/telemetry /var/opt/tansu/telemetry
VOLUME ["/var/opt/tansu/telemetry"]
ENTRYPOINT ["dotnet", "TansuCloud.Telemetry.dll"]
