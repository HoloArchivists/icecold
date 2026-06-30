# Icecold

> ⚠️ Vibecoded slopware. Proceed with caution.

Icecold is a cold-archive torrent gateway. It lets you point at a large backing store,
index files into individual torrents, and hand out `.torrent` files or magnet links
without keeping a separate tracker, webseed server, and always-on seeder stack.

The intended niche is sparse downloads from very large collections: archives, datasets,
mirrors, media libraries, or other stores where most files are cold most of the time.
Icecold can act as the first source of bytes from the backing store, then let normal
BitTorrent peers take over as clients begin sharing with each other.

## What It Does

- Indexes files from a configured archive or content directory.
- Generates one BitTorrent torrent per indexed file.
- Serves `.torrent` files and magnet links for ready content.
- Runs a built-in tracker so clients can discover each other.
- Provides cold file bytes from the backing store through HTTP WebSeed and TCP peer-wire seeding with optional MSE/RC4 protocol encryption.
- Runs as a local ASP.NET Core app or a Docker image.

## Current Limits

- Only single-file indexing is implemented. Folder indexing currently returns `501 Not Implemented`.
- Peer-wire support is upload-only TCP, including `ut_metadata` for magnet clients and MSE/RC4 protocol encryption. Icecold does not download from peers, initiate outbound peer connections, or support uTP yet.
- The tracker peer store is in-memory and single-instance.
- The only implemented content source is the local filesystem.
- S3 or HTTP-backed storage should fit the existing `IContentSource` boundary, but those sources are not implemented yet.
- This is not hardened as a public multi-tenant service.

## Quick Production Deployment

Published images are built by GitHub Actions and pushed to:

```text
ghcr.io/holoarchivists/icecold
```

Use [compose.prod.yaml](compose.prod.yaml) for a single-host deployment. It runs the
published API image and a PostgreSQL container, mounts your archive read-only into the API
container, and applies EF migrations on startup by default.

Create a `.env` file next to `compose.prod.yaml`:

```bash
POSTGRES_PASSWORD=replace-with-a-long-random-password
ICECOLD_ADMIN_API_KEY=replace-with-a-long-random-admin-key
ICECOLD_PUBLIC_BASE_URL=https://icecold.example.org
ICECOLD_CONTENT_ROOT=/srv/archive/files
ICECOLD_HTTP_PORT=8080
ICECOLD_PEERWIRE_ADVERTISED_IP=203.0.113.10
ICECOLD_PEERWIRE_PORT=6881
ICECOLD_PEERWIRE_ADVERTISED_PORT=6881
ICECOLD_IMAGE_TAG=latest
# Optional: run the API container as a host user that can read ICECOLD_CONTENT_ROOT.
ICECOLD_UID=1000
ICECOLD_GID=1000
```

Start it:

```bash
docker compose -f compose.prod.yaml up -d
```

The production compose file intentionally requires `POSTGRES_PASSWORD`,
`ICECOLD_ADMIN_API_KEY`, `ICECOLD_PUBLIC_BASE_URL`, `ICECOLD_CONTENT_ROOT`, and
`ICECOLD_PEERWIRE_ADVERTISED_IP`.
`ICECOLD_CONTENT_ROOT` is mounted read-only at `/data/files` inside the API container.
The advertised peer-wire address must be an IP address clients can reach; BitTorrent
tracker compact peer responses cannot advertise a DNS name.
Set `ICECOLD_UID` and `ICECOLD_GID` if the API container needs a specific host identity
to read the mounted content root.

Set `ICECOLD_AUTO_MIGRATE=false` if you want to manage database migrations separately.
For internet-facing deployments, put Icecold behind a reverse proxy or load balancer that
terminates TLS and forwards HTTP traffic to `ICECOLD_HTTP_PORT`. Peer-wire uses plain TCP
on `ICECOLD_PEERWIRE_PORT` and must be reachable directly by BitTorrent clients.

## Use The API

Index a file from the configured content source:

```bash
curl -i http://localhost:8080/api/index/file \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key' \
  -d '{"source":"local","path":"example.txt"}'
```

Poll the returned `/api/torrents/{id}` URL until `status` is `Ready`, then use:

```bash
curl -OJ http://localhost:8080/torrents/{infoHash}.torrent
curl http://localhost:8080/torrents/{infoHash}/magnet
```

Submitting the same `{ source, path }` again is idempotent after path normalization and metadata lookup:

- `Pending` or `Hashing`: returns the existing record with `202 Accepted`.
- `Ready` or `Duplicate`: returns the existing record with `200 OK`.
- `Failed`: resets the existing record to `Pending`, clears the error, and retries it.
- Changed file metadata creates a new record because it represents new content.

If different source files build the same BitTorrent info hash, the first completed row remains
the canonical `Ready` torrent and later rows become `Duplicate` aliases with `duplicateOfId`
pointing to the canonical row.

## Development

The default [compose.yaml](compose.yaml) is for local development, not production.
It uses the .NET SDK image, mounts this repo at `/workspace`, applies EF migrations,
and runs the API with `dotnet watch`. The API container runs as
`${ICECOLD_UID}:${ICECOLD_GID}` to avoid root-owned build artifacts in the bind-mounted repo.

```bash
docker compose up
```

If your host user is not `1000:1000`, create a local `.env` first:

```bash
printf 'ICECOLD_UID=%s\nICECOLD_GID=%s\n' "$(id -u)" "$(id -g)" > .env
```

The dev API is available at:

```text
http://localhost:5038
```

Swagger UI is available in local development at:

```text
http://localhost:5038/swagger
```

The raw OpenAPI documents are available at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

For peer-wire testing through dev Compose, set `Icecold:PeerWire:AdvertisedIp` in
`src/Icecold.Api/appsettings.Development.json` to an IP address reachable by your torrent
client. The dev compose file enables the listener but intentionally leaves the advertised
peer endpoint to app configuration.

Dev Compose runs a Debug build by default. For peer-wire throughput testing, use the
published production image or run the dev container in Release mode:

```bash
ICECOLD_DOTNET_CONFIGURATION=Release docker compose up
```

To create a sample file under the default dev content source:

```bash
mkdir -p src/Icecold.Api/data/files
printf 'hello from icecold\n' > src/Icecold.Api/data/files/example.txt
```

For manual local development without the API container, start only PostgreSQL:

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

PostgreSQL is exposed on host port `55432` to avoid colliding with an existing local
Postgres on `5432`.

## Build A Local Image

For a quick local image build from the repo root:

```bash
docker build -t icecold-api:local .
```

## Configuration

Main settings live under `Icecold` in `appsettings.json`:

- `PublicBaseUrl`: base URL embedded into tracker and webseed metadata.
- `AdminApiKey`: required value for private `/api/*` endpoints. The development key lives only in `appsettings.Development.json`; non-development deployments must provide a real value through environment, secret, or external configuration.
- `Indexing:MaxConcurrency`: number of concurrent hashing workers.
- `Tracker:*`: announce intervals, peer timeout, and max peer response size.
- `PeerWire:*`: upload-only TCP peer-wire listener, advertised peer endpoint, block size, connection cap, and request queue tuning.
- `ContentSources`: named sources. V1 supports `Type: "local"` with a required, non-blank `RootPath`.

`Database:AutoMigrate` defaults to `false` in app configuration. The production Compose file
sets it to `true` by default through `ICECOLD_AUTO_MIGRATE`; set `ICECOLD_AUTO_MIGRATE=false`
if you want to manage migrations yourself.

## Test

```bash
dotnet test Icecold.slnx
```
