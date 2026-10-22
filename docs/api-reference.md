# API Reference

Every endpoint Icecold exposes. For how to put them together in a catalog, see the [integration guide](integration-guide.md).

- [Conventions](#conventions)
- Admin API
  - [`POST /api/index/file`](#post-apiindexfile): index one file
  - [`POST /api/index/files`](#post-apiindexfiles): index several files as one multi-file torrent
  - [`POST /api/index/folder`](#post-apiindexfolder): not implemented
  - [`GET /api/torrents/{id}`](#get-apitorrentsid): status and metadata
  - [`PUT /api/torrents/{id}/priority`](#put-apitorrentsidpriority): set a queued job's priority
  - [`GET /api/torrents/{id}/locations`](#get-apitorrentsidlocations): list backing locations
  - [`POST /api/torrents/{id}/locations`](#post-apitorrentsidlocations): add a backing location
  - [`POST /api/torrents/{id}/locations/{locationId}/primary`](#post-apitorrentsidlocationslocationidprimary): make a location primary
  - [`DELETE /api/torrents/{id}/locations/{locationId}`](#delete-apitorrentsidlocationslocationid): disable a location
- Public endpoints
  - [`GET /torrents/{infoHash}.torrent`](#get-torrentsinfohashtorrent)
  - [`GET /torrents/{infoHash}/magnet`](#get-torrentsinfohashmagnet)
  - [`GET|HEAD /webseed/{infoHash}/{path}`](#gethead-webseedinfohashpath)
  - [`GET /announce`, `GET /scrape`](#get-announce-get-scrape)
  - [`GET /stats`](#get-stats)
  - [`GET /health`](#get-health)

## Conventions

**Authentication.** Every `/api/*` endpoint requires the header `X-Icecold-Admin-Key` set to `Icecold:AdminApiKey`. A missing or wrong key returns `401` with an empty body. If the server has no key configured, admin endpoints return `500` (and in `Production` the server refuses to start). The public endpoints need no authentication.

**JSON.** Requests and responses use camelCase JSON. Timestamps are ISO 8601 with offset.

**Errors.** Error bodies depend on where the request was rejected:

| Status | Body |
|---|---|
| `400` from request validation (malformed JSON, missing required field) | ASP.NET validation problem: `{"title": "...", "status": 400, "errors": {"field": ["message"]}}` |
| `400` / `404` from Icecold | `{"error": "message"}` |
| `409` | RFC 7807 problem: `{"title": "...", "status": 409, "detail": "message"}` |
| `401`, plain `404` | empty |

**Identifiers.** `id` is Icecold's UUID for an index record. `infoHash` is the torrent's 40-character lowercase hex SHA-1 v1 info hash, available once the record is `Ready` or `Duplicate`. Public endpoints accept upper- or lowercase hex.

**Development tooling.** In the `Development` environment, Swagger UI is served at `/swagger` and OpenAPI documents at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

## Torrent Record

Indexing endpoints and `GET /api/torrents/{id}` return this object:

```json
{
  "id": "f8cc8522-5f70-44a3-a02e-7b96b3eb6650",
  "source": "local",
  "path": "shows/my-show/ep01.mkv",
  "displayName": "ep01.mkv",
  "contentLength": 3000000,
  "status": "Ready",
  "priority": 0,
  "queuePosition": null,
  "hashedBytes": null,
  "infoHash": "ddeda3f42828a2f1b1dea15821decc3e26365106",
  "duplicateOfId": null,
  "pieceLength": 262144,
  "pieceCount": 12,
  "error": null,
  "locations": [ /* location objects, primary first */ ],
  "files": null
}
```

| Field | Meaning |
|---|---|
| `id` | Record id. Stable; use it to poll. |
| `source` | Content source name the record was indexed from. |
| `path` | Normalized path within the source. For multi-file torrents, a `manifest:<sha256>` identity instead (see [multi-file torrents](multi-file-torrents.md#identity-and-resubmission)). |
| `displayName` | Torrent name: the file name for single-file torrents, the top-level folder for multi-file ones. |
| `contentLength` | Total bytes of the backing file(s). |
| `status` | See [statuses](#statuses). |
| `priority` | Queue priority. Higher is hashed first. |
| `queuePosition` | While `Pending`: jobs that will run before this one at current priorities (`0` = next). Otherwise `null`. |
| `hashedBytes` | While `Hashing`: bytes hashed so far, out of `contentLength`, persisted every few seconds. Otherwise `null`. |
| `infoHash` | Set once `Ready` or `Duplicate`. |
| `duplicateOfId` | For `Duplicate` records: the id of the canonical `Ready` record with the same info hash. |
| `pieceLength`, `pieceCount` | Torrent geometry, set once hashed. |
| `error` | Reason for the last failure or retry, if any. |
| `locations` | Backing locations attached to this record (single-file torrents). Empty for multi-file torrents. |
| `files` | Multi-file torrents only: `[{ "path", "torrentPath", "contentLength" }]` in torrent order. `null` for single-file torrents. |

### Statuses

| Status | Meaning |
|---|---|
| `Pending` | Queued for hashing. Also the state of a job waiting out a retry backoff. |
| `Hashing` | A worker is hashing it. |
| `Ready` | Torrent is available. `infoHash` is the canonical torrent. |
| `Duplicate` | Hashed to an info hash that another `Ready` record already owns. `infoHash` is that same hash and `duplicateOfId` points at the canonical record, so the torrent is just as downloadable. A single-file duplicate's file is added as an alternate location of the canonical record. |
| `Failed` | Gave up. Reason in `error`. Resubmitting resets it to `Pending`. |

Retryable failures (backing store unreachable, transient database errors) send the job back to `Pending` with exponential backoff, up to 5 attempts, before it becomes `Failed`. Permanent failures (file changed while hashing, unreadable file) fail immediately.

---

## Admin API

### `POST /api/index/file`

Index one file as a single-file torrent.

```bash
curl -i https://icecold.example.org/api/index/file \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: $ICECOLD_ADMIN_API_KEY' \
  -d '{"source":"local","path":"shows/my-show/ep01.mkv","displayName":"My Show - 01.mkv","priority":0}'
```

| Field | Required | |
|---|---|---|
| `source` | yes | Configured content source name. |
| `path` | yes | Path relative to the source root (or `BaseUrl` for HTTP sources), at most 4096 characters. Normalized, so `./a.txt` and `a.txt` are the same file. |
| `displayName` | no | Torrent name. Defaults to the file name. At most 512 characters. |
| `priority` | no | Default `0`. Higher runs first; within a priority, jobs run in submission order. |

Icecold stats the file before answering, so a missing file is reported immediately.

| Status | When |
|---|---|
| `202 Accepted` | Queued (new, still queued, or a `Failed` record reset). `Location: /api/torrents/{id}`. Body is the [torrent record](#torrent-record) with `queuePosition`. |
| `200 OK` | Already `Ready` or `Duplicate`. Body is the record with `infoHash`. |
| `400` | Missing fields, path too long, path outside the source root, symlink, zero-length file, or other unreadable content. |
| `404` | Unknown source, or file not found. |
| `409` | Concurrent submissions of the same file could not be serialized. Retry. |

**Idempotency.** A submission is matched on `(source, normalized path, length, version)`, where the version comes from the source (local: size and modification time; HTTP: strong `ETag` or `Last-Modified`). Resubmitting the same unchanged file returns the existing record:

| Existing status | Result |
|---|---|
| `Pending` / `Hashing` | `202` with the existing record. A higher `priority` raises the job's priority; a lower one is ignored. |
| `Ready` / `Duplicate` | `200` with the existing record. `displayName` is ignored. |
| `Failed` | Reset to `Pending` (attempts and error cleared), `202`. |

If the file has changed since it was indexed, it is a different version and gets a new record (and a new info hash).

### `POST /api/index/files`

Index an explicit list of files from one source as one multi-file torrent. See [multi-file torrents](multi-file-torrents.md) for layout, validation rules and serving behavior.

```bash
curl -i https://icecold.example.org/api/index/files \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: $ICECOLD_ADMIN_API_KEY' \
  -d '{
    "source": "local",
    "name": "My Show",
    "files": [
      { "path": "shows/my-show/ep01.mkv" },
      { "path": "shows/my-show/ep01.ass", "torrentPath": "subs/ep01.ass" }
    ],
    "priority": 0
  }'
```

| Field | Required | |
|---|---|---|
| `source` | yes | Content source name. All files come from this source. |
| `name` | yes | Torrent name, the top-level folder clients create. One path segment, at most 512 characters. |
| `files` | yes | 1 to 1000 entries, in torrent order. |
| `files[].path` | yes | Path within the source. |
| `files[].torrentPath` | no | Slash-separated path inside `name`. Defaults to the file's name. |
| `priority` | no | As for `/api/index/file`. |

Responses and idempotency are the same as `POST /api/index/file`. The record is matched on the name and the ordered list of `(path, torrentPath, length, version)`. `400` also covers the file-list validation rules.

### `POST /api/index/folder`

Reserved for folder discovery. Always returns `501 Not Implemented`. List the files explicitly with [`/api/index/files`](#post-apiindexfiles) instead.

### `GET /api/torrents/{id}`

Returns the [torrent record](#torrent-record), including `queuePosition` while `Pending` and `hashedBytes` while `Hashing`. `404` if the id is unknown. This is the endpoint to poll after submitting.

### `PUT /api/torrents/{id}/priority`

Set the priority of a queued job exactly. Unlike resubmitting, this can also lower it.

```bash
curl -i -X PUT https://icecold.example.org/api/torrents/{id}/priority \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: $ICECOLD_ADMIN_API_KEY' \
  -d '{"priority":10}'
```

| Status | When |
|---|---|
| `200` | Updated. Body is the torrent record. |
| `404` | Unknown id. |
| `409` | The job is no longer `Pending` or `Hashing`. |

A job that is already `Hashing` keeps hashing. The new priority matters only if it is requeued.

### Backing Locations

A single-file torrent can be served from several copies of the same bytes, for example a hot SSD path and a cold archive path. Icecold serves from the primary location and fails over to the others when it is missing or changed. Multi-file torrents don't support locations.

Location endpoints operate on the canonical record. Passing the id of a `Duplicate` record acts on the record it duplicates.

**Location object:**

```json
{
  "id": "0f6c...",
  "torrentId": "f8cc...",
  "source": "cold",
  "path": "archive/ep01.mkv",
  "contentLength": 3000000,
  "contentVersion": "00000000002dc6c0-08dd...",
  "contentLastModified": "2026-10-01T12:00:00+00:00",
  "status": "Active",
  "isPrimary": false,
  "priority": 10,
  "lastVerifiedAt": "2026-10-22T09:00:00+00:00",
  "lastError": null,
  "createdAt": "2026-10-01T12:00:00+00:00",
  "updatedAt": "2026-10-22T09:00:00+00:00"
}
```

| Location status | Meaning |
|---|---|
| `Active` | Verified and servable. |
| `Missing` | The file or source was not found on the last attempt. Retried only after the active locations fail. |
| `Stale` | The file changed (length or version differs) or could not be read. Retried only after the active locations fail. |
| `Disabled` | Disabled by an admin. Never served. |

Serving order is: active before unhealthy, primary first, then ascending `priority`, then oldest. A location that recovers is marked `Active` again when it next serves.

#### `GET /api/torrents/{id}/locations`

Returns the location objects, primary first. `404` if the id is unknown.

#### `POST /api/torrents/{id}/locations`

Attach another copy of the file. Icecold hashes the new copy and only accepts it if it produces the same info hash, so expect this request to take as long as hashing the file.

```bash
curl -i https://icecold.example.org/api/torrents/{id}/locations \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: $ICECOLD_ADMIN_API_KEY' \
  -d '{"source":"cold","path":"archive/ep01.mkv","makePrimary":false,"priority":10}'
```

| Field | Required | |
|---|---|---|
| `source` | yes | Content source name, at most 128 characters. |
| `path` | yes | Path within the source, at most 4096 characters. |
| `makePrimary` | no | Default `false`. The location also becomes primary if there is no active primary. |
| `priority` | no | Failover order among non-primary locations, lower first. Defaults to 10 more than the current highest. |

Adding a location that already exists (same source, path, length and version) re-verifies and reactivates it.

| Status | When |
|---|---|
| `200` | Added or refreshed. Body is the location object. |
| `400` | Unknown source, or the file is unreadable. |
| `404` | Unknown torrent id, or file not found. |
| `409` | Torrent not `Ready`; the file hashes to a different info hash; the torrent is multi-file; or a concurrent change (retry). |

#### `POST /api/torrents/{id}/locations/{locationId}/primary`

Make an `Active` location primary after re-checking its length and version. Returns `200` with the location, `404` if either id is unknown, or `409` if the location is not active or fails verification (in which case its status is updated).

#### `DELETE /api/torrents/{id}/locations/{locationId}`

Soft-disable a location: it becomes `Disabled` and is never served again. If it was the primary, the best remaining active location is promoted. Returns `200` with the disabled location, or `404`. To use the location again, add it again with `POST /api/torrents/{id}/locations`.

---

## Public Endpoints

These are what BitTorrent clients and end users hit. They only expose `Ready` torrents.

### `GET /torrents/{infoHash}.torrent`

Downloads the `.torrent` file (`application/x-bittorrent`, file name `{displayName}.torrent`). The tracker and webseed URLs are filled in from the current configuration on each request, so changing `PublicBaseUrl`, tracker or webseed settings never changes info hashes. `400` for a malformed hash, `404` if no `Ready` torrent has it.

### `GET /torrents/{infoHash}/magnet`

Returns a magnet link as `text/plain`, with `xt`, `dn` (display name), `xl` (content length), one `tr` per tracker URL, and `ws` (webseed) when webseeding is enabled. Clients fetch the info dictionary from Icecold's peer-wire (`ut_metadata`) or from other peers, so a magnet link needs peer-wire or an existing swarm. Hand out the `.torrent` file if you only run webseeds. `400` / `404` as above.

### `GET|HEAD /webseed/{infoHash}/{path}`

BEP 19 webseed. Clients build these URLs from the torrent's `url-list`; you don't normally link them directly.

- Single-file torrents: `/webseed/{infoHash}/{displayName}`. Anything after the info hash is accepted.
- Multi-file torrents: `/webseed/{infoHash}/{name}/{torrentPath}`.

Supports a single `Range: bytes=...` range. Responses carry `Accept-Ranges: bytes`, `ETag: "{infoHash}"` and `Cache-Control: public, max-age=86400`, since the bytes behind an info hash never change.

| Status | When |
|---|---|
| `200` / `206` | Full body / requested range. Invalid or multi-range `Range` headers get the full body. |
| `400` | Malformed info hash. |
| `404` | Webseed disabled, unknown torrent, or no such file in a multi-file torrent. |
| `416` | Range outside the file. `Content-Range: bytes */{length}`. |
| `503` | Torrent is known but no backing location is readable right now. `Retry-After: 60`. |

### `GET /announce`, `GET /scrape`

The built-in BitTorrent HTTP tracker (BEP 3, compact peers per BEP 23), enabled by `Icecold:Tracker:Enabled`. Responses are bencoded `text/plain`. Errors are bencoded `failure reason` responses with status `200`, as clients expect. Both return `404` when the tracker is disabled.

`/announce` requires `info_hash`, `peer_id` (20 raw bytes each, URL-encoded), `port`, `uploaded`, `downloaded` and `left`. It accepts `compact`, `numwant` (default 50) and `event`. `/scrape` accepts one or more `info_hash` parameters. Announces for info hashes that are not `Ready` in Icecold fail with `unregistered torrent`. When peer-wire is enabled, Icecold adds itself to peer lists (`PeerWire:AdvertisedIp`/`AdvertisedPort`), unless [standby mode](configuration.md#peer-wire-behavior) has stepped back because enough external seeders exist.

### `GET /stats`

Public aggregate numbers, enabled by `Icecold:Stats:Enabled` (otherwise `404`). Cached server-side for `Stats:CacheSeconds`, with a matching `Cache-Control` header.

```json
{
  "generatedAt": "2026-10-22T09:00:00+00:00",
  "cacheSeconds": 5,
  "torrents": { "ready": 1200, "seeding": 37, "readyBytes": 5400000000000, "seedingBytes": 120000000000, "largestReadyBytes": 80000000000 },
  "tracker": { "activeInfoHashes": 37, "peers": 210, "seeders": 90, "leechers": 120, "completedAnnounces": 5100 },
  "serving": { "webSeedEnabled": true, "peerWireEnabled": true }
}
```

Source names, location health, failure counts and private endpoints are deliberately left out.

### `GET /health`

Liveness probe. Returns `200 {"status":"ok"}` without touching the database.
