# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Packages stay in the restore layer rather than a cache mount: CI restores layers from the GitHub
# Actions cache but not cache mounts, so a cached restore layer would otherwise reach publish with
# an empty package folder.
COPY src/Icecold.Api/Icecold.Api.csproj src/Icecold.Api/
RUN dotnet restore src/Icecold.Api/Icecold.Api.csproj

COPY src/ src/
RUN dotnet publish src/Icecold.Api/Icecold.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080 6881

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "Icecold.Api.dll"]
