# syntax=docker/dockerfile:1

# Build and publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TansuCloud.Dashboard/TansuCloud.Dashboard.csproj", "TansuCloud.Dashboard/"]
RUN dotnet restore "./TansuCloud.Dashboard/TansuCloud.Dashboard.csproj"
COPY . .
WORKDIR "/src/TansuCloud.Dashboard"
RUN dotnet publish "./TansuCloud.Dashboard.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final runtime image (chiseled, non-root by default)
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS final
ARG REPOSITORY_URL=https://github.com/MusaGursoy/TansuCloud
ARG BUILD_VERSION=0.0.0
ARG BUILD_COMMIT=unknown
ARG BUILD_DATE=unknown
LABEL org.opencontainers.image.source=$REPOSITORY_URL \
      org.opencontainers.image.url=$REPOSITORY_URL \
      org.opencontainers.image.title="TansuCloud.Dashboard" \
      org.opencontainers.image.description="TansuCloud Dashboard service." \
      org.opencontainers.image.version=$BUILD_VERSION \
      org.opencontainers.image.revision=$BUILD_COMMIT \
      org.opencontainers.image.created=$BUILD_DATE
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TansuCloud.Dashboard.dll"]