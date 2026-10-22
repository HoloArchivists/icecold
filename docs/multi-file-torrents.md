# Multi-File Torrents

[`POST /api/index/files`](api-reference.md#post-apiindexfiles) bundles an explicit list of files into one torrent, for example an episode with its subtitles, or a release with its NFO and samples. Icecold doesn't crawl directories: you name every file, which works the same for local and HTTP sources.

```json
{
  "source": "local",
  "name": "My Show",
  "files": [
    { "path": "shows/my-show/ep01.mkv" },
    { "path": "shows/my-show/ep01.ass", "torrentPath": "subs/ep01.ass" },
    { "path": "shows/my-show/extras/clip.bin", "torrentPath": "extras/clip.bin" }
  ]
}
```

A client downloading this torrent creates:

```text
My Show/
├── ep01.mkv
├── subs/ep01.ass
└── extras/clip.bin
```

## Rules

- All files come from one content source.
- Files appear in the torrent in request order. The order is part of the info hash.
- `name` is a single path segment. `torrentPath` is a `/`-separated path below it and defaults to the file's name.
- `name` and every `torrentPath` segment must be a valid file name on any OS: not empty, not `.` or `..`, no `/`, `\` or control characters, at most 255 UTF-8 bytes.
- No two `torrentPath`s may be equal ignoring case, since clients on Windows and macOS would collide. A path can't be both a file and a directory (`a` and `a/b`).
- The top-level directory `.pad` is reserved for padding (below).
- Zero-length files are rejected.
- At most 1000 files per torrent.

Breaking any of these returns `400 {"error": "..."}` naming the offending path. An unknown source or a missing file returns `404`.

## Padding

Each file except the last is followed by a [BEP 47](https://www.bittorrent.org/beps/bep_0047.html) pad file (`attr: "p"`, path `.pad/<length>`) that runs to the next piece boundary. So every file starts on a piece boundary and no piece contains bytes from two files. That keeps reads from the backing store simple, especially from HTTP origins, and a client that wants only some files never downloads pieces of the others.

Clients that understand pad files (libtorrent-based clients such as qBittorrent and Deluge, Transmission 4, and others) hide them and never download them. Older clients show a `.pad` folder and download zero bytes into it. That costs bandwidth but does no harm.

Piece length is chosen from the total size of the files, the same way as for single files (256 KiB up to 64 MiB, then 1, 4 and 16 MiB).

## Identity and Resubmission

The record's `path` is `manifest:<sha256>`, a hash of the name plus each file's source path, `torrentPath`, length and version. Resubmitting the same request while nothing has changed returns the existing record, with the same [idempotency rules](api-reference.md#post-apiindexfile) as single files. Changing the order, a `torrentPath`, the name, or any file's content produces a new record and a new info hash.

`displayName` is the torrent name and `contentLength` is the sum of the file sizes, padding excluded. The `files` array lists the files in torrent order:

```json
"files": [
  { "path": "shows/my-show/ep01.mkv", "torrentPath": "ep01.mkv", "contentLength": 3000000 },
  { "path": "shows/my-show/ep01.ass", "torrentPath": "subs/ep01.ass", "contentLength": 777 }
]
```

## Serving

- **Webseed:** the torrent's `url-list` is `/webseed/{infoHash}/`. Per BEP 19, clients append the torrent name and each file's path, for example `/webseed/{infoHash}/My%20Show/subs/ep01.ass`.
- **Peer-wire:** blocks are read from the file that contains them. Pad bytes are generated as zeros, not read from storage.
- **Change detection:** before serving, Icecold checks every file's length and version. If any file has changed or disappeared, the whole torrent becomes unavailable (webseed `503`, peer-wire refuses the connection), so clients never receive bytes that don't match the piece hashes. Resubmit the request to index the new content as a new torrent.

## Limitations

- No [backing locations](api-reference.md#backing-locations): a multi-file torrent is served only from the files it was indexed from, with no alternate copies or failover. `POST /api/torrents/{id}/locations` returns `409`.
- Every new peer-wire connection re-checks every file. That's cheap for local sources and is absorbed by the metadata cache (`MetadataCacheSeconds`) for HTTP sources. For HTTP torrents with many files, keep that cache time reasonable.
- The same file indexed on its own and as part of a multi-file torrent ends up in two different torrents with separate swarms.
