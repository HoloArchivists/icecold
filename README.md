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

## Documentation

- [Integration guide](docs/integration-guide.md): connect your catalog to Icecold
- [API reference](docs/api-reference.md): all admin and public endpoints
- [Multi-file torrents](docs/multi-file-torrents.md)
- [Deployment](docs/deployment.md)
- [Configuration](docs/configuration.md)
- [Development](docs/development.md)

## What It Does

- Indexes files from a configured archive or content directory
- Generates one BitTorrent torrent per indexed file, or one multi-file torrent per explicit file list
- Serves `.torrent` files and magnet links for ready content
- Runs an optional built-in tracker so clients can discover each other
- Seeds file bytes from the backing store via configurable HTTP WebSeed and/or TCP peer-wire, with optional MSE/RC4 encryption
- Tracks multiple verified backing locations per torrent, with failover from hot to cold paths as files are moved
- Runs as a local ASP.NET Core app or a Docker image

## Limitations

- Files are indexed one at a time or as an explicit list bundled into one multi-file torrent. Folder discovery (`/api/index/folder`) returns `501 Not Implemented`.
- Multi-file torrents are served only from the files they were indexed from: no alternate backing locations or failover.
- Peer-wire is upload-only TCP, including `ut_metadata` for magnet clients and MSE/RC4 encryption. Icecold does not download from peers, initiate outbound connections, or support uTP.
- If the built-in tracker is disabled, WebSeed is required for Icecold-origin serving. Icecold peer-wire will not be discoverable because Icecold does not announce itself through DHT, PEX, or external trackers.
- The tracker peer store is in-memory and single-instance.
- Local filesystem and HTTP origin content sources are implemented. S3 would fit the existing `IContentSource` interface but is not built yet.
- Public multi-tenant hardening is out of scope.

## Quick Start

Run the production image with PostgreSQL (see [deployment](docs/deployment.md) for the `.env` settings):

```bash
docker compose -f compose.prod.yaml up -d
```

Index a file from the configured content root, poll until it is `Ready`, then hand out the torrent:

```bash
curl -i http://localhost:8080/api/index/file \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key' \
  -d '{"source":"local","path":"example.txt"}'

curl http://localhost:8080/api/torrents/{id} -H 'X-Icecold-Admin-Key: replace-with-a-long-random-admin-key'

curl -OJ http://localhost:8080/torrents/{infoHash}.torrent
curl http://localhost:8080/torrents/{infoHash}/magnet
```

To bundle several files into one torrent, use [`/api/index/files`](docs/multi-file-torrents.md). The [integration guide](docs/integration-guide.md) covers on-demand and bulk indexing, priorities, status handling and backing locations.
