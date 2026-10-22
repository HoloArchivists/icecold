# Integration Guide

How to put Icecold behind an existing catalog, archive site or dataset index so that users get a torrent for any item, indexed on demand or ahead of time. Endpoint details are in the [API reference](api-reference.md).

## The Model

```text
your catalog item ──(source, path)──▶ Icecold record ──(once Ready)──▶ infoHash ──▶ .torrent / magnet
```

- A **content source** is a named root that Icecold reads from: a local directory or an HTTP origin, configured in `Icecold:ContentSources` (see [configuration](configuration.md)). Your catalog refers to files by `(source, path)` relative to that root, and can never make Icecold read outside it.
- **Indexing** hashes the file into a torrent. It runs in a database-backed queue and can take a while for large files, so it's asynchronous: you submit, then poll.
- A **record** (`id`) tracks one indexing request. Once `Ready`, it has an `infoHash`, which is what you hand to users.
- **Serving** is automatic once a torrent is ready. Clients get bytes from Icecold over webseed and/or peer-wire, find each other through the built-in tracker, and share among themselves.

Keep these rules in mind:

1. **The admin API is server-to-server.** The `X-Icecold-Admin-Key` lets the holder make Icecold hash any file under its sources. Call `/api/*` from your backend only, never from browsers. Public pages should only link the public `/torrents/...` URLs.
2. **Submissions are idempotent.** Submitting the same unchanged file again returns the same record. You can treat "submit" as "get or create" and don't need to remember whether you submitted before.
3. **An info hash is permanent.** The bytes behind an info hash never change. If a file changes, it's a new version with a new record and a new info hash.

## Workflow 1: On Demand

Index a file the first time someone asks for it. Good for very large archives where most items are never downloaded.

```text
user clicks "Download torrent"
  └─▶ backend: POST /api/index/file {source, path, priority: 100}
        ├─ 200 Ready/Duplicate ─▶ redirect to /torrents/{infoHash}.torrent
        ├─ 202 Pending/Hashing ─▶ show "preparing (position N / X% hashed)", poll GET /api/torrents/{id}
        │                           └─ Ready ─▶ redirect; Failed ─▶ show error
        └─ 404 ─▶ item no longer exists in the archive
```

Give interactive requests a high priority so they jump ahead of background work. Resubmitting a queued job only ever raises its priority, so every click can safely resubmit with the "user is waiting" priority.

`queuePosition` (while `Pending`) and `hashedBytes / contentLength` (while `Hashing`) are enough for a progress indicator. Hashing runs at roughly disk or origin read speed, so a multi-GB file takes seconds to minutes.

## Workflow 2: Pre-Index

Index everything (or everything popular) ahead of time so downloads are instant.

1. Walk your catalog and submit each item with priority `0`. Run submissions concurrently; each one is a database write plus a stat of the file.
2. Store the returned `id` next to the catalog item.
3. Periodically poll the records that are not yet `Ready` (or poll lazily when a user views the item), and store `infoHash` once it's set.
4. Use workflow 1 as a fallback for items that aren't ready yet: resubmitting with a higher priority bumps them.

Icecold has no list or search endpoint. Keep the `id`/`infoHash` mapping in your catalog. If you lose it, resubmitting the same `(source, path)` returns the record again.

## What to Store

| Field | Why |
|---|---|
| `id` | To poll status and manage locations. |
| `infoHash` | To build public links. Never changes for this version of the file. |
| `status` | To know whether to show a download link or a "preparing" state. |
| `contentLength`, `displayName` | Optional, for display. |

Build public links from the info hash:

```text
https://icecold.example.org/torrents/{infoHash}.torrent
https://icecold.example.org/torrents/{infoHash}/magnet     (returns the magnet URI as text)
```

Prefer the `.torrent` link. It carries the tracker and webseed URLs and works even if a client can't reach Icecold's peer-wire port. A magnet link needs the client to fetch the torrent's info dictionary from a peer first (Icecold's peer-wire or the existing swarm). If you only run webseeds, use `.torrent` links. If you want magnets on the page, fetch `/torrents/{infoHash}/magnet` from your backend and cache the string. It only changes if Icecold's public URLs or tracker settings change.

## Handling Each Status

| Status | Show users | Do |
|---|---|---|
| `Pending` | "Queued" (with `queuePosition`) | Poll every few seconds while someone is waiting. |
| `Hashing` | "Preparing" (with progress) | Poll. |
| `Ready` | Download link | Store `infoHash`. |
| `Duplicate` | Download link | Same as `Ready`: `infoHash` is the canonical torrent's. Happens when two paths contain identical bytes and the same torrent name. |
| `Failed` | Error | Read `error`. Resubmitting resets the record and retries, e.g. after fixing the file or origin. |

Transient problems (origin down, database blip) are retried by Icecold itself with backoff, up to 5 attempts, while the record stays `Pending`. You only see `Failed` after that, or for permanent problems such as a file that changed during hashing.

When a backing file becomes unreadable after the torrent is ready, webseed requests return `503` with `Retry-After` and Icecold keeps the record `Ready`. Fix the storage, or see [moving files](#moving-files) below.

## Bundling Files

To ship several files as one torrent (an episode with its subtitles, a dataset split into parts), use `POST /api/index/files` with an explicit list. Store and link the result exactly like a single file. See [multi-file torrents](multi-file-torrents.md) for the rules.

## Changing Files

Content behind an info hash never changes. If you replace a file in the archive:

- The old torrent stops serving: its location is marked `Stale` (single file) or the torrent becomes unavailable (multi-file), so clients never get mismatched bytes.
- Submitting the path again creates a **new** record with a new info hash. Update your catalog to the new `infoHash`.

## Moving Files

To move a single-file item between storage tiers or paths without changing its info hash, use [backing locations](api-reference.md#backing-locations):

1. Copy the file to its new place.
2. `POST /api/torrents/{id}/locations` with the new `(source, path)` and `makePrimary: true`. Icecold re-hashes the copy and only accepts it if the bytes match.
3. `DELETE /api/torrents/{id}/locations/{oldLocationId}` once you remove the old copy, or keep both: Icecold fails over from the primary to other locations automatically.

Multi-file torrents don't support locations. Resubmit them from the new paths instead, which creates a new torrent.

## Example Client (Python)

```python
import time
import requests

ICECOLD = "https://icecold.example.org"
ADMIN = {"X-Icecold-Admin-Key": "replace-with-admin-key"}


def ensure_torrent(source: str, path: str, priority: int = 0, wait_seconds: float = 0) -> dict:
    """Submit (or look up) a file and optionally wait until it is ready."""
    r = requests.post(f"{ICECOLD}/api/index/file", headers=ADMIN,
                      json={"source": source, "path": path, "priority": priority}, timeout=30)
    if r.status_code == 409:  # concurrent submissions of the same file; just retry
        return ensure_torrent(source, path, priority, wait_seconds)
    if r.status_code in (400, 404):
        raise ValueError(r.json()["error"])
    r.raise_for_status()
    record = r.json()

    deadline = time.monotonic() + wait_seconds
    while record["status"] in ("Pending", "Hashing") and time.monotonic() < deadline:
        time.sleep(2)
        record = requests.get(f"{ICECOLD}/api/torrents/{record['id']}", headers=ADMIN, timeout=30).json()
    return record


record = ensure_torrent("local", "shows/my-show/ep01.mkv", priority=100, wait_seconds=60)
if record["status"] in ("Ready", "Duplicate"):
    print(f"{ICECOLD}/torrents/{record['infoHash']}.torrent")
elif record["status"] == "Failed":
    print("failed:", record["error"])
else:
    print("still preparing:", record["status"], record.get("queuePosition"), record.get("hashedBytes"))
```

## Checklist

- [ ] Content sources configured and readable by the Icecold process ([configuration](configuration.md)).
- [ ] `PublicBaseUrl` is the public HTTPS origin users and clients reach. It is embedded in tracker and webseed URLs.
- [ ] The admin key is kept in your backend's secrets, and `/api/*` is not exposed to browsers. Optionally restrict it at the reverse proxy too.
- [ ] Your catalog stores `id` and `infoHash`.
- [ ] Interactive requests use a higher priority than bulk indexing.
- [ ] Peer-wire port reachable from the internet if you enable it ([deployment](deployment.md)), or you hand out `.torrent` files and rely on webseeds.
