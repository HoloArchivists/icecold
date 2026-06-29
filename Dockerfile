# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Icecold.Api/Icecold.Api.csproj src/Icecold.Api/
RUN dotnet restore src/Icecold.Api/Icecold.Api.csproj

COPY . .
RUN dotnet publish src/Icecold.Api/Icecold.Api.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "Icecold.Api.dll"]
