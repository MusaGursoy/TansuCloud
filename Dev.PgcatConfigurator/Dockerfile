# build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ./*.sln ./
COPY Dev.PgcatConfigurator/ Dev.PgcatConfigurator/
RUN dotnet restore ./Dev.PgcatConfigurator/Dev.PgcatConfigurator.csproj
RUN dotnet publish ./Dev.PgcatConfigurator/Dev.PgcatConfigurator.csproj -c Release -o /app/publish --no-restore

# runtime
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Dev.PgcatConfigurator.dll"]
