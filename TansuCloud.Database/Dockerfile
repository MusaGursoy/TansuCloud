# syntax=docker/dockerfile:1

# Build and publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TansuCloud.Database/TansuCloud.Database.csproj", "TansuCloud.Database/"]
RUN dotnet restore "./TansuCloud.Database/TansuCloud.Database.csproj"
COPY . .
WORKDIR "/src/TansuCloud.Database"
RUN dotnet publish "./TansuCloud.Database.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final runtime image (chiseled, non-root by default)
FROM busybox:stable-musl AS bb
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS final
ARG REPOSITORY_URL=https://github.com/MusaGursoy/TansuCloud
ARG BUILD_VERSION=0.0.0
ARG BUILD_COMMIT=unknown
ARG BUILD_DATE=unknown
LABEL org.opencontainers.image.source=$REPOSITORY_URL \
      org.opencontainers.image.url=$REPOSITORY_URL \
      org.opencontainers.image.title="TansuCloud.Database" \
      org.opencontainers.image.description="TansuCloud Database service." \
      org.opencontainers.image.version=$BUILD_VERSION \
      org.opencontainers.image.revision=$BUILD_COMMIT \
      org.opencontainers.image.created=$BUILD_DATE
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish .
COPY --from=bb /bin/busybox /bin/busybox
ENTRYPOINT ["dotnet", "TansuCloud.Database.dll"]