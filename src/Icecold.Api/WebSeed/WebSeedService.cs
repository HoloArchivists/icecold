using Icecold.Api.Content;
using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Icecold.Api.WebSeed;

public sealed class WebSeedService(IcecoldDbContext db, ContentSourceRegistry sources)
{
    public async Task<WebSeedOpenResult> OpenAsync(
        string infoHash,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var normalized = infoHash.ToLowerInvariant();
        var torrent = await db.Torrents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.InfoHashHex == normalized && t.Status == TorrentStatus.Ready, cancellationToken);

        if (torrent is null)
            return WebSeedOpenResult.NotFound();

        var source = sources.GetRequired(torrent.SourceName);
        var metadata = await source.GetMetadataAsync(torrent.SourcePath, cancellationToken);
        if (metadata.Length != torrent.ContentLength || metadata.Version != torrent.ContentVersion)
            return WebSeedOpenResult.Conflict("Backing content changed since this torrent was indexed.");

        var (offset, length, partial) = ParseRange(rangeHeader, metadata.Length);
        var stream = partial
            ? await source.OpenRangeAsync(metadata.Path, offset, length, cancellationToken)
            : await source.OpenReadAsync(metadata.Path, cancellationToken);

        return WebSeedOpenResult.Opened(stream, offset, length, metadata.Length, partial);
    }

    static (long Offset, long Length, bool Partial) ParseRange(string? rangeHeader, long contentLength)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return (0, contentLength, false);

        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed) || parsed.Ranges.Count != 1)
            throw new BadHttpRequestException("Only a single valid byte range is supported.");

        var range = parsed.Ranges.Single();
        long start;
        long end;

        if (range.From.HasValue)
        {
            start = range.From.Value;
            end = range.To ?? contentLength - 1;
        }
        else if (range.To.HasValue)
        {
            var suffixLength = Math.Min(range.To.Value, contentLength);
            start = contentLength - suffixLength;
            end = contentLength - 1;
        }
        else
        {
            throw new BadHttpRequestException("Invalid byte range.");
        }

        if (start < 0 || start >= contentLength || end < start)
            throw new BadHttpRequestException("Requested range is not satisfiable.");

        end = Math.Min(end, contentLength - 1);
        return (start, end - start + 1, true);
    }
}

public sealed record WebSeedOpenResult(
    WebSeedOpenStatus Status,
    Stream? Stream,
    long Offset,
    long Length,
    long ContentLength,
    bool Partial,
    string? Error)
{
    public static WebSeedOpenResult Opened(Stream stream, long offset, long length, long contentLength, bool partial)
        => new(WebSeedOpenStatus.Opened, stream, offset, length, contentLength, partial, null);

    public static WebSeedOpenResult NotFound()
        => new(WebSeedOpenStatus.NotFound, null, 0, 0, 0, false, null);

    public static WebSeedOpenResult Conflict(string error)
        => new(WebSeedOpenStatus.Conflict, null, 0, 0, 0, false, error);
}

public enum WebSeedOpenStatus
{
    Opened,
    NotFound,
    Conflict
}
