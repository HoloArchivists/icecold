# Deployment

Running Icecold in production with Docker Compose. For settings beyond what `compose.prod.yaml` maps, see [configuration](configuration.md).

Published images are built by GitHub Actions:

```text
ghcr.io/holoarchivists/icecold
```

Use [compose.prod.yaml](../compose.prod.yaml) for a single-host deployment. It runs the API image and a PostgreSQL container, mounts your archive read-only, and applies EF migrations on startup.

## Environment Variables

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

## Start

```bash
docker compose -f compose.prod.yaml up -d
```

## Reverse Proxy

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

## Notes

- `ICECOLD_CONTENT_ROOT` is mounted read-only at `/data/files` inside the container.
- Set `ICECOLD_AUTO_MIGRATE=false` to manage database migrations separately.
- Run a single Icecold instance per database, because the tracker peer store is in memory. The indexing queue itself lives in the database: workers claim jobs under a lease they renew while hashing, and a job whose worker died is picked up again once its lease (`Indexing:LeaseDurationSeconds`, default 60) expires. On graceful shutdown in-progress jobs are handed back immediately.
- `compose.prod.yaml` maps deployment essentials. Use a Compose override for other app-level settings such as super-seeding, standby mode, indexing concurrency, and tracker limits.
- Peer-wire uses plain TCP on `ICECOLD_PEERWIRE_PORT` and must be reachable directly by BitTorrent clients, not through an HTTP proxy or CDN. Open it in any host or cloud firewall.
- Peer-wire limits connections per client address (`PeerWire:MaxConnectionsPerIp`, default 8). If Docker's userland proxy handles the published port (for example IPv6 clients reaching an IPv4-only container), every peer appears to come from the bridge gateway and they all share that limit. If that happens, set `"userland-proxy": false` in the Docker daemon config, or run the API service with `network_mode: host`.
- The PostgreSQL service uses the `postgres:18` image, which stores data under `/var/lib/postgresql/18/docker` in the volume. Upgrading an existing volume from an older major version needs `pg_upgrade` or a dump and restore.
- Container logs are rotated by the Docker `json-file` driver (5 × 20 MB per service).
