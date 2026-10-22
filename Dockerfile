# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Icecold.Api/Icecold.Api.csproj src/Icecold.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/Icecold.Api/Icecold.Api.csproj

COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Icecold.Api/Icecold.Api.csproj \
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
