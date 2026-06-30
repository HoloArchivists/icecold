# Icecold

> ⚠️ Vibecoded slopware. Proceed with caution.

Icecold is a cold-archive torrent gateway. Point it at a large backing store, index files into individual torrents, and hand out `.torrent` files or magnet links — no separate tracker, webseed server, or always-on seeder stack needed.

The intended niche is sparse downloads from very large collections: archives, datasets, mirrors, and media libraries where most files are cold most of the time. Icecold seeds bytes directly from the backing store, then lets normal BitTorrent peers take over as clients begin sharing with each other.

## Contents

- [What It Does](#what-it-does)
- [Limitations](#limitations)
- [Deployment](#deployment)
- [API](#api)
- [Configuration](#configuration)
- [Development](#development)

## What It Does

- Indexes files from a configured archive or content directory
- Generates one BitTorrent torrent per indexed file
- Serves `.torrent` files and magnet links for ready content
- Runs a built-in tracker so clients can discover each other
- Seeds file bytes from the backing store via HTTP WebSeed and TCP peer-wire, with optional MSE/RC4 encryption
- Tracks multiple verified backing locations per torrent, with failover from hot to cold paths as files are moved
- Runs as a local ASP.NET Core app or a Docker image

## Limitations

- Only single-file indexing is implemented. Folder indexing returns `501 Not Implemented`.
- Peer-wire is upload-only TCP, including `ut_metadata` for magnet clients and MSE/RC4 encryption. Icecold does not download from peers, initiate outbound connections, or support uTP.
- The tracker peer store is in-memory and single-instance.
- Only local filesystem content sources are implemented. S3 and HTTP backends would fit the existing `IContentSource` interface but are not built yet.
- Not hardened for public multi-tenant use.

## Deployment

Published images are built by GitHub Actions:

```text
ghcr.io/holoarchivists/icecold
```

Use [compose.prod.yaml](compose.prod.yaml) for a single-host deployment. It runs the API image and a PostgreSQL container, mounts your archive read-only, and applies EF migrations on startup.

### Environment Variables

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

The first five variables and `ICECOLD_PEERWIRE_ADVERTISED_IP` are required. The advertised peer-wire address must be an IP — BitTorrent compact peer responses can't carry a DNS name. Set `ICECOLD_UID`/`ICECOLD_GID` if the container needs a specific host identity to read the content root.

### Start

```bash
docker compose -f compose.prod.yaml up -d
```

### Notes

- `ICECOLD_CONTENT_ROOT` is mounted read-only at `/data/files` inside the container.
- Set `ICECOLD_AUTO_MIGRATE=false` to manage database migrations separately.
- For internet-facing deployments, put Icecold behind a reverse proxy that terminates TLS. Peer-wire uses plain TCP on `ICECOLD_PEERWIRE_PORT` and must be reachable directly by BitTorrent clients.

## API

### Index a File

```bash
curl -i http://localhost:8080/api/index/file \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key' \
  -d '{"source":"local","path":"example.txt"}'
```

Poll the returned `/api/torrents/{id}` URL until `status` is `Ready`.

### Download

```bash
# .torrent file
curl -OJ http://localhost:8080/torrents/{infoHash}.torrent

# Magnet link
curl http://localhost:8080/torrents/{infoHash}/magnet
```

### Idempotency

Re-submitting the same `{ source, path }` is idempotent after path normalization and metadata lookup:

| Existing status | Behavior |
|---|---|
| `Pending` or `Hashing` | Returns the existing record with `202 Accepted` |
| `Ready` or `Duplicate` | Returns the existing record with `200 OK` |
| `Failed` | Resets to `Pending`, clears the error, and retries |

If different source files produce the same info hash, the first completed row stays canonical (`Ready`) and later ones become `Duplicate` aliases with `duplicateOfId` pointing to it. The duplicate's source path is also recorded as an alternate backing location for the canonical torrent.

### Backing Locations

```bash
# List locations
curl http://localhost:8080/api/torrents/{id}/locations \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key'

# Add a location
curl -i http://localhost:8080/api/torrents/{id}/locations \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key' \
  -d '{"source":"cold","path":"example.txt","makePrimary":false}'

# Promote to primary
curl -i -X POST http://localhost:8080/api/torrents/{id}/locations/{locationId}/primary \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key'

# Remove (soft-disable)
curl -i -X DELETE http://localhost:8080/api/torrents/{id}/locations/{locationId} \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key'
```

Adding a location hashes the file and only accepts it if it produces the same info hash with the torrent's display name. Serving prefers the primary location, then lower-priority active locations, then retries stale or missing ones as active candidates fail.

## Configuration

Settings live under `Icecold` in `appsettings.json`:

| Key | Default | Description |
|---|---|---|
| `PublicBaseUrl` | — | Base URL embedded into tracker announce URLs and `.torrent` webseed metadata |
| `AdminApiKey` | — | Required in the `X-Icecold-Admin-Key` header for all `/api/*` endpoints. Absent in base config — provide via environment variable or secret in production; `appsettings.Development.json` sets it to `dev-admin-key` |
| `Database:AutoMigrate` | `false` | Run pending EF Core migrations automatically on startup. The production Compose file sets this via `ICECOLD_AUTO_MIGRATE` |
| `Indexing:MaxConcurrency` | `1` | Number of parallel hashing workers processing the indexing queue |
| `Indexing:QueueCapacity` | `10000` | Maximum pending indexing jobs held in memory before new submissions block |
| `Tracker:AnnounceIntervalSeconds` | `1800` | Recommended reannounce interval returned to clients (30 min) |
| `Tracker:MinAnnounceIntervalSeconds` | `300` | Minimum reannounce interval returned to clients (5 min) |
| `Tracker:PeerTimeoutSeconds` | `2700` | Time after which a peer that has stopped announcing is removed from the in-memory store (45 min) |
| `Tracker:MaxPeersReturned` | `200` | Maximum number of peers returned in a single announce response |
| `Tracker:MaxPeersStoredPerTorrent` | `1000` | Maximum peers retained per infohash before evicting least-recently announced peers |
| `Tracker:PruneIntervalSeconds` | `300` | Background pruning interval for expired peers and empty infohash buckets |
| `PeerWire:Enabled` | `false` | Enable the upload-only TCP peer-wire seeding listener |
| `PeerWire:BindAddress` | `0.0.0.0` | Local IP to bind the listener; `0.0.0.0` listens on all interfaces |
| `PeerWire:ListenPort` | `6881` | TCP port to accept incoming peer connections on |
| `PeerWire:AdvertisedIp` | — | Public IP advertised in tracker responses. Must be an IP address — BitTorrent compact peer responses cannot carry DNS names. Required when `Enabled` is true |
| `PeerWire:AdvertisedPort` | `6881` | Public port advertised in tracker responses. Can differ from `ListenPort` when behind NAT |
| `PeerWire:MaxBlockLength` | `16384` | Maximum piece block size accepted from peers, in bytes (16 KiB is the BitTorrent standard) |
| `PeerWire:MaxOutstandingRequests` | `8192` | Advertised as `reqq` in the extension handshake — how many requests a peer may pipeline |
| `PeerWire:MaxConnections` | `128` | Maximum concurrent peer connections; new connections are rejected once this is reached |
| `PeerWire:HandshakeTimeoutSeconds` | `15` | Timeout for completing the BitTorrent or MSE/RC4 handshake before dropping a connection |
| `PeerWire:IdleTimeoutSeconds` | `120` | Timeout before an idle connection with no messages (including keepalives) is closed |
| `ContentSources[].Name` | — | Unique source identifier (case-insensitive) used in indexing requests and stored with each torrent |
| `ContentSources[].Type` | `local` | Source implementation type. Only `local` is supported; S3 and HTTP sources are not yet available |
| `ContentSources[].RootPath` | — | Base directory for a local source. Relative paths are resolved from the application content root. If `ContentSources` is omitted entirely, a default `local` source is created at `{ContentRootPath}/data/files` |

## Development

The default [compose.yaml](compose.yaml) uses the .NET SDK image, mounts this repo at `/workspace`, applies EF migrations, and runs the API with `dotnet watch`.

```bash
docker compose up
```

If your host user is not `1000:1000`, create a `.env` first:

```bash
printf 'ICECOLD_UID=%s\nICECOLD_GID=%s\n' "$(id -u)" "$(id -g)" > .env
```

The dev API is at `http://localhost:5038`. Swagger UI is at `http://localhost:5038/swagger`. Raw OpenAPI docs are at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

### Peer-Wire

For peer-wire testing through dev Compose, set `Icecold:PeerWire:AdvertisedIp` in `appsettings.Development.json` to an IP your torrent client can reach. Dev Compose enables the listener but leaves the advertised endpoint to app config.

Dev Compose uses a Debug build by default. For throughput testing, use the production image or run in Release mode:

```bash
ICECOLD_DOTNET_CONFIGURATION=Release docker compose up
```

### Sample Content

```bash
mkdir -p src/Icecold.Api/data/files
printf 'hello from icecold\n' > src/Icecold.Api/data/files/example.txt
```

### Running Without Docker

Start only PostgreSQL (exposed on host port `55432` to avoid conflicting with a local instance on `5432`):

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

### Local Image Build

```bash
docker build -t icecold-api:local .
```

### Tests

```bash
dotnet test Icecold.slnx
```

### Load and E2E Tests

The load runner in [tests/Icecold.LoadTests](tests/Icecold.LoadTests) prints a JSON report and can write it to a file with `--output`.

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
