# Configuration

How to configure Icecold, and what startup validation enforces.

The canonical option reference is [appsettings.json](../src/Icecold.Api/appsettings.json). It lists the available settings and their defaults. For local development, `appsettings.Development.json` can override those values. For containers, use environment variables or a Compose override.

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

For production, `compose.prod.yaml` already maps reverse proxy, webseed origin, and peer-wire basics from `.env`. Put other app-level customizations in a Compose override:

```yaml
# compose.prod.override.yaml
services:
  api:
    environment:
      Icecold__WebSeed__Enabled: "true"
      Icecold__Tracker__Enabled: "true"
      Icecold__Tracker__AdditionalAnnounceUrls__0: "udp://tracker.example.net:6969/announce"
      Icecold__Tracker__AdditionalAnnounceUrls__1: "https://backup.example.test/announce"
      Icecold__Tracker__MaxPeersPerSourceIpPerTorrent: "8"
      Icecold__PeerWire__MaxConnectionsPerIp: "8"
      Icecold__PeerWire__SuperSeed__Enabled: "true"
      Icecold__PeerWire__SuperSeed__MinimumPeerCount: "3"
      Icecold__PeerWire__Standby__Enabled: "true"
      Icecold__PeerWire__Standby__MinimumExternalSeeders: "2"
```

Run with both files:

```bash
docker compose -f compose.prod.yaml -f compose.prod.override.yaml up -d
```

## Content Sources

Each entry in `Icecold:ContentSources` names a root that index requests refer to by `source`. `Type` is `local` (a directory, `RootPath`) or `http` (an origin, `BaseUrl`). Paths in requests are always relative to that root: absolute paths, `..` escapes and, for local sources, symlinks are rejected.

```yaml
Icecold__ContentSources__0__Name: "local"
Icecold__ContentSources__0__Type: "local"
Icecold__ContentSources__0__RootPath: "/data/files"
```

Local sources use the file's size and modification time as its version, so touching a file without changing it still counts as a new version.

## Networking and Validation

If peer-wire is enabled, `Icecold:PeerWire:AdvertisedIp` must be an IP address, not a hostname. BitTorrent compact peer responses cannot carry DNS names.

If Icecold is behind an HTTP reverse proxy, enable `Icecold:ReverseProxy:Enabled` and configure `KnownProxies` or `KnownNetworks` with the proxy connection source IPs. Icecold only trusts `X-Forwarded-For` and `X-Forwarded-Proto` from those sources. Configure the proxy or Docker network range, not public client ranges.

Additional tracker URLs are appended after Icecold's own `/announce` URL in generated `.torrent` files and magnet links while `Icecold:Tracker:Enabled` is true. If the built-in tracker is disabled, Icecold's `/announce` URL is omitted and only `Icecold:Tracker:AdditionalAnnounceUrls` are emitted.

Startup validation also rejects:

- an empty `Icecold:AdminApiKey` when `ASPNETCORE_ENVIRONMENT=Production`
- `PublicBaseUrl` or `WebSeed:PublicBaseUrl` values that are not absolute `http`/`https` URLs, or that carry a query string or fragment
- tracker timings where `MinAnnounceIntervalSeconds` exceeds `AnnounceIntervalSeconds`, or where `PeerTimeoutSeconds` is not longer than `AnnounceIntervalSeconds`
- `Stats:CacheSeconds` outside 1–300

Public endpoints have per-client abuse limits. The tracker caps stored peers per client address per torrent (`Tracker:MaxPeersPerSourceIpPerTorrent`) and rate-limits announces per client address per torrent. Peer-wire caps concurrent connections per client address (`PeerWire:MaxConnectionsPerIp`) and enforces an overall handshake deadline. IPv6 clients are grouped by /64 for these limits.

Startup validation requires at least one enabled serving transport: `Icecold:WebSeed:Enabled` or `Icecold:PeerWire:Enabled`.

When `Icecold:Tracker:Enabled` is false, startup validation also requires `Icecold:WebSeed:Enabled=true`. Peer-wire can still listen, but clients will not discover Icecold as a peer through generated torrent metadata because Icecold does not use DHT, PEX, or announce itself to external trackers.

## Peer-Wire Behavior

Super-seeding is intended for torrents with multiple leechers. With only one leecher, Icecold still makes progress by revealing the next piece after the current advertised piece has been fully uploaded, but throughput can be lower than normal peer-wire mode because the client only sees a small piece window. Threshold-based super-seeding only affects new peer-wire connections; a client that already received the full bitfield stays in normal mode until it reconnects.

Swarm standby mode uses the built-in tracker's external seeder count. Leechers with partial data are ignored for standby decisions, so Icecold keeps advertising/uploading until enough non-Icecold peers announce `left=0`.

## HTTP Content Sources

An `http` source serves files from a static HTTP origin such as nginx, an S3-compatible endpoint with public or header-authenticated access, or another mirror. Paths in index requests are relative to `BaseUrl`, so callers can only name files under that prefix, never arbitrary URLs.

```yaml
Icecold__ContentSources__1__Name: "mirror"
Icecold__ContentSources__1__Type: "http"
Icecold__ContentSources__1__BaseUrl: "https://origin.example.org/datasets/"
Icecold__ContentSources__1__Headers__Authorization: "Bearer ..."   # optional
```

| Setting | Default | |
|---|---|---|
| `BaseUrl` | | Absolute `http`/`https` URL without query, fragment or credentials. |
| `Headers` | | Extra request headers, e.g. `Authorization`. |
| `RequestTimeoutSeconds` | 30 | Limit for connecting and receiving response headers, and for each body read. |
| `MaxConnections` | 32 | Connections to the origin. |
| `MetadataCacheSeconds` | 5 | How long `HEAD` results are reused. |
| `ReadAheadBytes` | 1048576 | Window fetched per range request when peer-wire reads 16 KiB blocks. |
| `MaxResumeAttempts` | 3 | Reconnects allowed without progress before a body read fails. |

The origin must:

- send a strong `ETag` or a `Last-Modified` header. Icecold uses it as the content version and sends it back as `If-Match`/`If-Unmodified-Since` on every read, so a file replaced on the origin fails reads and its location is marked stale instead of serving mixed bytes;
- support byte ranges (`Accept-Ranges: bytes`) for WebSeed and peer-wire serving and for resuming dropped downloads while hashing. Indexing works without ranges, but a dropped connection then restarts the hash;
- serve the raw bytes (Icecold asks for `Accept-Encoding: identity`) and answer directly. Redirects are not followed.

`HEAD` is used for metadata, with a one-byte range `GET` as a fallback for origins that reject `HEAD`. A `404`/`410` marks a location missing. Timeouts, `408`, `429` and `5xx` are treated as temporary: indexing retries them with backoff, and serving fails over to another location.
