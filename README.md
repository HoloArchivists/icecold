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

- Lets an admin request indexing for a file in a configured content source.
- Hashes the file in the background and stores the resulting torrent metadata.
- Serves `.torrent` files and magnet links for ready content.
- Helps download clients find each other through its built-in tracker.
- Provides the first copy of cold files from the backing store when no peers have them yet.
- Reuses existing records when the same file version is indexed again.
- Avoids duplicate torrent storage when different indexed files resolve to the same content.
- Protects local file sources with a configured root, traversal checks, and symlink rejection.
- Runs as a local ASP.NET Core app or as a Docker image.

## Current Limits

- Only single-file indexing is implemented. Folder indexing currently returns `501 Not Implemented`.
- The tracker peer store is in-memory and single-instance.
- The only implemented content source is the local filesystem.
- S3 or HTTP-backed storage should fit the existing `IContentSource` boundary, but those sources are not implemented yet.
- This is not hardened as a public multi-tenant service.

## Run Locally

Start PostgreSQL:

```bash
docker compose up -d postgres
```

The compose file maps PostgreSQL to host port `55432` to avoid colliding with an existing local Postgres on `5432`.

Restore tools and apply the schema:

```bash
dotnet tool restore
dotnet tool run dotnet-ef database update \
  --project src/Icecold.Api/Icecold.Api.csproj \
  --startup-project src/Icecold.Api/Icecold.Api.csproj
```

Create a sample file under the configured local source. Relative source roots are resolved from the API content root:

```bash
mkdir -p src/Icecold.Api/data/files
printf 'hello from icecold\n' > src/Icecold.Api/data/files/example.txt
```

Run the service:

```bash
dotnet run --project src/Icecold.Api/Icecold.Api.csproj --launch-profile http
```

Swagger UI is available in local development at:

```text
http://localhost:5038/swagger
```

The raw OpenAPI documents are available at `/swagger/v1/swagger.json` and `/openapi/v1.json`.

## Docker

Build the API image from the repo root:

```bash
docker build -t icecold-api:local .
```

Run it against the local PostgreSQL container and mount the local file source into the container:

```bash
docker run --rm -p 18080:8080 \
  --add-host=host.docker.internal:host-gateway \
  -v "$PWD/src/Icecold.Api/data/files:/data/files:ro" \
  -e ConnectionStrings__Icecold='Host=host.docker.internal;Port=55432;Database=icecold;Username=icecold;Password=icecold' \
  -e Icecold__PublicBaseUrl='http://localhost:18080' \
  -e Icecold__AdminApiKey='replace-with-a-secret' \
  -e Icecold__ContentSources__0__Name='local' \
  -e Icecold__ContentSources__0__Type='local' \
  -e Icecold__ContentSources__0__RootPath='/data/files' \
  icecold-api:local
```

Mount real content roots into the container and point `Icecold__ContentSources__0__RootPath`
at the mounted path before indexing. The Docker build context intentionally ignores local data
directories so large backing stores are not copied into the image.

Index a file:

```bash
curl -i http://localhost:5038/api/index/file \
  -H 'Content-Type: application/json' \
  -H 'X-Icecold-Admin-Key: dev-admin-key' \
  -d '{"source":"local","path":"example.txt"}'
```

For the Docker command above, use `http://localhost:18080` and the value supplied through
`Icecold__AdminApiKey`.

Poll the returned `/api/torrents/{id}` URL until `status` is `Ready`, then use:

```bash
curl -OJ http://localhost:5038/torrents/{infoHash}.torrent
curl http://localhost:5038/torrents/{infoHash}/magnet
```

Submitting the same `{ source, path }` again is idempotent after path normalization and metadata lookup:

- `Pending` or `Hashing`: returns the existing record with `202 Accepted`.
- `Ready` or `Duplicate`: returns the existing record with `200 OK`.
- `Failed`: resets the existing record to `Pending`, clears the error, and retries it.
- Changed file metadata creates a new record because it represents new content.

If different source files build the same BitTorrent info hash, the first completed row remains
the canonical `Ready` torrent and later rows become `Duplicate` aliases with `duplicateOfId`
pointing to the canonical row.

## Configuration

Main settings live under `Icecold` in `appsettings.json`:

- `PublicBaseUrl`: base URL embedded into tracker and webseed metadata.
- `AdminApiKey`: required value for private `/api/*` endpoints. The development key lives only in `appsettings.Development.json`; non-development deployments must provide a real value through environment, secret, or external configuration.
- `Indexing:MaxConcurrency`: number of concurrent hashing workers.
- `Tracker:*`: announce intervals, peer timeout, and max peer response size.
- `ContentSources`: named sources. V1 supports `Type: "local"` with a required, non-blank `RootPath`.

`Database:AutoMigrate` is available but defaults to `false`; use the EF CLI migration command above for local setup.

## Test

```bash
dotnet test Icecold.slnx
```
