# Icecold

> ⚠️ Vibecoded slopware. Proceed with caution.

Icecold is a cold-archive torrent gateway. Point it at a large backing store, index files into individual torrents, and hand out `.torrent` files or magnet links with tracker, webseed, and initial peer-wire seeding built in.

The intended niche is sparse downloads from very large collections: archives, datasets, mirrors, and media libraries where most files are cold most of the time. Icecold seeds bytes directly from the backing store, then lets normal BitTorrent peers take over as clients begin sharing with each other.

Icecold is for you if:

- You want to publish many individual files as torrents without manually managing millions of `.torrent` files
- You want one service that indexes content, stores torrent metadata, can run the tracker, and seeds the initial bytes
- You control the tracker URL and want clients for the same archive to find each other through your service
- Most content is cold, but any file may need to become available on demand
- You want the archive backing store to remain the source of truth while peer swarms opportunistically absorb download traffic

Icecold may not fit if:

- You want to seed existing torrents to public trackers
- You need a general-purpose BitTorrent client that downloads, repairs, or ratio-manages torrents
- You need DHT, PEX, outbound peer dialing, uTP, or multi-tracker swarm participation
- You want a distributed tracker or multi-node peer store today
- You only need to serve a small number of hot torrents from a normal seedbox

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
- Runs an optional built-in tracker so clients can discover each other
- Seeds file bytes from the backing store via configurable HTTP WebSeed and/or TCP peer-wire, with optional MSE/RC4 encryption
- Tracks multiple verified backing locations per torrent, with failover from hot to cold paths as files are moved
- Runs as a local ASP.NET Core app or a Docker image

## Limitations

- Single-file indexing is implemented. Folder indexing returns `501 Not Implemented`.
- Peer-wire is upload-only TCP, including `ut_metadata` for magnet clients and MSE/RC4 encryption. Icecold does not download from peers, initiate outbound connections, or support uTP.
- If the built-in tracker is disabled, WebSeed is required for Icecold-origin serving. Icecold peer-wire will not be discoverable because Icecold does not announce itself through DHT, PEX, or external trackers.
- The tracker peer store is in-memory and single-instance.
- Local filesystem content sources are implemented. S3 and HTTP backends would fit the existing `IContentSource` interface but are not built yet.
- Public multi-tenant hardening is out of scope.

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

# Optional Docker/runtime settings
ICECOLD_IMAGE_TAG=latest          # or a release tag such as 1.2.0
ICECOLD_HTTP_BIND=127.0.0.1       # host address for the HTTP port; keep loopback behind a reverse proxy
ICECOLD_HTTP_PORT=8080
ICECOLD_UID=1654                  # host identity that can read ICECOLD_CONTENT_ROOT
ICECOLD_GID=1654
ICECOLD_ALLOWED_HOSTS=icecold.example.org
ICECOLD_LOG_FORMAT=simple         # or json
ICECOLD_AUTO_MIGRATE=true

# Reverse proxy trust (enabled by default; trusts the Compose network gateway)
ICECOLD_REVERSE_PROXY_ENABLED=true
ICECOLD_NETWORK_SUBNET=172.31.250.0/24

# Optional separate public origin for /webseed (for example a non-CDN hostname)
ICECOLD_WEBSEED_PUBLIC_BASE_URL=

# Peer-wire seeding
ICECOLD_PEERWIRE_ENABLED=false
ICECOLD_PEERWIRE_ADVERTISED_IP=   # public IP address, required when peer-wire is enabled
ICECOLD_PEERWIRE_PORT=6881
```

`POSTGRES_PASSWORD`, `ICECOLD_ADMIN_API_KEY`, `ICECOLD_PUBLIC_BASE_URL`, and `ICECOLD_CONTENT_ROOT` are required. The container runs as UID/GID 1654 (the .NET image's `app` user) unless `ICECOLD_UID`/`ICECOLD_GID` are set; pick an identity that can read the content root.

### Start

```bash
docker compose -f compose.prod.yaml up -d
```

### Reverse Proxy

The HTTP port is published on `127.0.0.1` by default. Terminate TLS in a reverse proxy on the host and forward to it. Compose pins the network to `ICECOLD_NETWORK_SUBNET`, and Icecold trusts `X-Forwarded-For`/`X-Forwarded-Proto` only from that subnet, which is where host-side proxies appear from. If that subnet clashes with another Docker network on the host, change it.

The proxy must set `X-Forwarded-For` to the real client address. If it sits behind a CDN, restore the client IP first (for example nginx `real_ip_header CF-Connecting-IP`) and then send `X-Forwarded-For $remote_addr`, not `$proxy_add_x_forwarded_for`. Otherwise the tracker records every peer under the proxy's address.

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
location /webseed/ {
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
    proxy_read_timeout 300s;
}
```

If Icecold receives traffic directly with no proxy, set `ICECOLD_REVERSE_PROXY_ENABLED=false` and `ICECOLD_HTTP_BIND=0.0.0.0`.

### Notes

- `ICECOLD_CONTENT_ROOT` is mounted read-only at `/data/files` inside the container.
- Set `ICECOLD_AUTO_MIGRATE=false` to manage database migrations separately.
- Run a single Icecold instance per database. The tracker peer store is in memory, and on startup Icecold resets every in-progress indexing job to `Pending`, which would take over jobs owned by another live instance.
- `compose.prod.yaml` maps deployment essentials. Use a Compose override for other app-level settings such as super-seeding, standby mode, indexing concurrency, and tracker limits.
- Peer-wire uses plain TCP on `ICECOLD_PEERWIRE_PORT` and must be reachable directly by BitTorrent clients, not through an HTTP proxy or CDN. Open it in any host or cloud firewall.
- The PostgreSQL service uses the `postgres:18` image, which stores data under `/var/lib/postgresql/18/docker` in the volume. Upgrading an existing volume from an older major version needs `pg_upgrade` or a dump and restore.
- Container logs are rotated by the Docker `json-file` driver (5 × 20 MB per service).

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

### Stats

```bash
curl http://localhost:8080/stats
```

The stats endpoint is public when `Stats:Enabled` is true and cached briefly by the server. It exposes aggregate public-facing catalog and swarm numbers: ready/seeding torrents, indexed ready bytes, tracker peer counts, and enabled serving transports. Backing-source names, location health, indexing failure counts, and private advertised endpoints are intentionally omitted.

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

The canonical option reference is [appsettings.json](src/Icecold.Api/appsettings.json). It lists the available settings and their defaults. For local development, `appsettings.Development.json` can override those values. For containers, use environment variables or a Compose override.

ASP.NET Core maps nested configuration keys by replacing `:` with double underscores:

```text
Icecold:PeerWire:Enabled
Icecold__PeerWire__Enabled=true

Icecold:PeerWire:Standby:MinimumExternalSeeders
Icecold__PeerWire__Standby__MinimumExternalSeeders=2

Icecold:ContentSources:0:RootPath
Icecold__ContentSources__0__RootPath=/data/files

Icecold:Tracker:AdditionalAnnounceUrls:0
Icecold__Tracker__AdditionalAnnounceUrls__0=udp://tracker.example.net:6969/announce

Icecold:ReverseProxy:KnownNetworks:0
Icecold__ReverseProxy__KnownNetworks__0=172.20.0.0/16

ConnectionStrings:Icecold
ConnectionStrings__Icecold=Host=postgres;Port=5432;Database=icecold;Username=icecold;Password=...
```

For production, put app-level customizations in a Compose override:

```yaml
# compose.prod.override.yaml
services:
  api:
    environment:
      Icecold__WebSeed__Enabled: "true"
      Icecold__WebSeed__PublicBaseUrl: "https://cdn.example.org"
      Icecold__ReverseProxy__Enabled: "true"
      Icecold__ReverseProxy__KnownNetworks__0: "172.20.0.0/16"
      Icecold__Tracker__Enabled: "true"
      Icecold__Tracker__AdditionalAnnounceUrls__0: "udp://tracker.example.net:6969/announce"
      Icecold__Tracker__AdditionalAnnounceUrls__1: "https://backup.example.test/announce"
      Icecold__PeerWire__Enabled: "true"
      Icecold__PeerWire__AdvertisedIp: "203.0.113.10"
      Icecold__PeerWire__AdvertisedPort: "6881"
      Icecold__PeerWire__SuperSeed__Enabled: "true"
      Icecold__PeerWire__SuperSeed__MinimumPeerCount: "3"
      Icecold__PeerWire__Standby__Enabled: "true"
      Icecold__PeerWire__Standby__MinimumExternalSeeders: "2"
```

Run with both files:

```bash
docker compose -f compose.prod.yaml -f compose.prod.override.yaml up -d
```

If peer-wire is enabled, `Icecold:PeerWire:AdvertisedIp` must be an IP address, not a hostname. BitTorrent compact peer responses cannot carry DNS names.

If Icecold is behind an HTTP reverse proxy, enable `Icecold:ReverseProxy:Enabled` and configure `KnownProxies` or `KnownNetworks` with the proxy connection source IPs. Icecold only trusts `X-Forwarded-For` and `X-Forwarded-Proto` from those sources. Configure the proxy or Docker network range, not public client ranges.

Additional tracker URLs are appended after Icecold's own `/announce` URL in generated `.torrent` files and magnet links while `Icecold:Tracker:Enabled` is true. If the built-in tracker is disabled, Icecold's `/announce` URL is omitted and only `Icecold:Tracker:AdditionalAnnounceUrls` are emitted.

Startup validation requires at least one enabled serving transport: `Icecold:WebSeed:Enabled` or `Icecold:PeerWire:Enabled`.

When `Icecold:Tracker:Enabled` is false, startup validation also requires `Icecold:WebSeed:Enabled=true`. Peer-wire can still listen, but clients will not discover Icecold as a peer through generated torrent metadata because Icecold does not use DHT, PEX, or announce itself to external trackers.

Super-seeding is intended for torrents with multiple leechers. With only one leecher, Icecold still makes progress by revealing the next piece after the current advertised piece has been fully uploaded, but throughput can be lower than normal peer-wire mode because the client only sees a small piece window. Threshold-based super-seeding only affects new peer-wire connections; a client that already received the full bitfield stays in normal mode until it reconnects.

Swarm standby mode uses the built-in tracker's external seeder count. Leechers with partial data are ignored for standby decisions, so Icecold keeps advertising/uploading until enough non-Icecold peers announce `left=0`.

## Development

The default [compose.yaml](compose.yaml) uses the .NET SDK image, mounts this repo at `/workspace`, applies EF migrations, and runs the API with `dotnet watch`. App behavior comes from `appsettings.json`, `appsettings.Development.json`, and any local Compose override.

```bash
docker compose up
```

If your host user is not `1000:1000`, create a `.env` first:

```bash
printf 'ICECOLD_UID=%s\nICECOLD_GID=%s\n' "$(id -u)" "$(id -g)" > .env
```

The dev API is at `http://localhost:5038`. Swagger UI is at `http://localhost:5038/swagger`. Raw OpenAPI docs are at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

### Peer-Wire

For peer-wire testing through dev Compose, enable peer-wire and set `Icecold:PeerWire:AdvertisedIp` in `appsettings.Development.json` or a local `compose.override.yaml`. The advertised IP must be reachable by your torrent client.

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
