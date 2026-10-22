# Development

Working on Icecold itself.

The default [compose.yaml](../compose.yaml) uses the .NET SDK image, mounts this repo at `/workspace`, applies EF migrations, and runs the API with `dotnet watch`. App behavior comes from `appsettings.json`, `appsettings.Development.json`, and any local Compose override.

```bash
docker compose up
```

If your host user is not `1000:1000`, create a `.env` first:

```bash
printf 'ICECOLD_UID=%s\nICECOLD_GID=%s\n' "$(id -u)" "$(id -g)" > .env
```

The dev API is at `http://localhost:5038`. Swagger UI is at `http://localhost:5038/swagger`. Raw OpenAPI docs are at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

## Peer-Wire

For peer-wire testing through dev Compose, enable peer-wire and set `Icecold:PeerWire:AdvertisedIp` in `appsettings.Development.json` or a local `compose.override.yaml`. The advertised IP must be reachable by your torrent client.

Dev Compose uses a Debug build by default. For throughput testing, use the production image or run in Release mode:

```bash
ICECOLD_DOTNET_CONFIGURATION=Release docker compose up
```

## Sample Content

```bash
mkdir -p src/Icecold.Api/data/files
printf 'hello from icecold\n' > src/Icecold.Api/data/files/example.txt
```

## Running Without Docker

Start PostgreSQL by itself. Compose exposes it on host port `55432` to avoid conflicting with a local instance on `5432`:

```bash
docker compose up -d postgres
```

Then restore tools, apply the schema, and run the API:

```bash
dotnet tool restore
dotnet tool run dotnet-ef database update \
  --project src/Icecold.Api/Icecold.Api.csproj \
  --startup-project src/Icecold.Api/Icecold.Api.csproj
dotnet run --project src/Icecold.Api/Icecold.Api.csproj --launch-profile http
```

## Local Image Build

```bash
docker build -t icecold-api:local .
```

## Tests

```bash
dotnet test Icecold.slnx
```

Some tests start a PostgreSQL container through Testcontainers, so Docker must be available.

## Load and E2E Tests

The load runner in [tests/Icecold.LoadTests](../tests/Icecold.LoadTests) prints a JSON report and can write it to a file with `--output`.

**E2E smoke test** (real PostgreSQL via Testcontainers):

```bash
dotnet run -c Release --project tests/Icecold.LoadTests -- e2e-smoke --file-size-mib 64
```

**Indexing load** against a live instance:

```bash
dotnet run -c Release --project tests/Icecold.LoadTests -- index-many \
  --base-url http://localhost:5038 \
  --admin-key dev-admin-key \
  --content-root src/Icecold.Api/data/files \
  --files 1000 \
  --file-size-kib 64 \
  --concurrency 32
```

**WebSeed throughput:**

```bash
dotnet run -c Release --project tests/Icecold.LoadTests -- webseed-throughput \
  --base-url http://localhost:5038 \
  --content-root src/Icecold.Api/data/files \
  --file-size-mib 1024 \
  --clients 4
```

**Peer-wire throughput** (multi-leecher and MSE cases):

```bash
dotnet run -c Release --project tests/Icecold.LoadTests -- peerwire-throughput \
  --base-url http://localhost:5038 \
  --content-root src/Icecold.Api/data/files \
  --peer-host 127.0.0.1 \
  --peer-port 6881 \
  --file-size-mib 1024 \
  --clients 1 \
  --encrypted true \
  --outstanding 256
```

For peer-wire throughput tests, prefer a Release build:

```bash
ICECOLD_DOTNET_CONFIGURATION=Release docker compose up -d --force-recreate api
```
