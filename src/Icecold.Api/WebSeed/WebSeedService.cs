using Icecold.Api.Content;
using Icecold.Api.Torrents;
using Microsoft.Net.Http.Headers;

namespace Icecold.Api.WebSeed;

public sealed class WebSeedService(TorrentLocationService locations)
{
    // fileName is the request path after the infohash. Single-file torrents ignore it; for
    // multi-file torrents (BEP 19) it is the torrent name followed by the file's path.
    public async Task<WebSeedOpenResult> OpenAsync(
        string infoHash,
        string? fileName,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var attemptedLocations = new HashSet<Guid>();
        while (true)
        {
            var resolved = await locations.ResolveByInfoHashAsync(infoHash, cancellationToken, attemptedLocations);
            if (resolved is null)
            {
                if (attemptedLocations.Count > 0
                    || await locations.ReadyTorrentExistsByInfoHashAsync(infoHash, cancellationToken))
                {
                    return WebSeedOpenResult.Unavailable("No readable backing location is currently available for this torrent.");
                }

                return WebSeedOpenResult.NotFound();
            }

            var file = resolved.Layout.IsMultiFile
                ? FindMultiFileEntry(resolved, fileName)
                : resolved.Layout.Files[0];
            if (file is null)
                return WebSeedOpenResult.NotFound();

            var range = ParseRange(rangeHeader, file.Length);
            if (!range.Satisfiable)
                return WebSeedOpenResult.RangeNotSatisfiable(file.Length);

            try
            {
                var stream = range.Partial
                    ? await resolved.Source.OpenRangeAsync(file.SourcePath, range.Offset, range.Length, cancellationToken)
                    : await resolved.Source.OpenReadAsync(file.SourcePath, cancellationToken);

                return WebSeedOpenResult.Opened(stream, range.Offset, range.Length, file.Length, range.Partial);
            }
            catch (Exception ex) when (IsContentAccessFailure(ex))
            {
                attemptedLocations.Add(resolved.Location.Id);
                await locations.ReportLocationFailureAsync(resolved.Location.Id, ex, cancellationToken);
            }
        }
    }

    static TorrentContentFile? FindMultiFileEntry(TorrentServingLocation resolved, string? fileName)
    {
        var prefix = resolved.Torrent.DisplayName + "/";
        if (fileName is null || !fileName.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var torrentPath = fileName[prefix.Length..];
        return resolved.Layout.Files.FirstOrDefault(f => string.Equals(f.TorrentPath, torrentPath, StringComparison.Ordinal));
    }

    // Per RFC 9110 a server may ignore Range; invalid, non-bytes, and multi-range headers get the full 200 body.
    // Only a syntactically valid single byte range that misses the content is answered with 416.
    static WebSeedRangeSelection ParseRange(string? rangeHeader, long contentLength)
    {
        var full = new WebSeedRangeSelection(0, contentLength, Partial: false, Satisfiable: true);
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return full;

        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed)
            || !string.Equals(parsed.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase)
            || parsed.Ranges.Count != 1)
        {
            return full;
        }

        var range = parsed.Ranges.Single();
        long start;
        long end;

        if (range.From.HasValue)
        {
            if (range.To < range.From)
                return full;

            start = range.From.Value;
            end = range.To ?? contentLength - 1;
        }
        else if (range.To.HasValue)
        {
            if (range.To.Value <= 0)
                return WebSeedRangeSelection.NotSatisfiable;

            var suffixLength = Math.Min(range.To.Value, contentLength);
            start = contentLength - suffixLength;
            end = contentLength - 1;
        }
        else
        {
            return full;
        }

        if (start < 0 || start >= contentLength || end < start)
            return WebSeedRangeSelection.NotSatisfiable;

        end = Math.Min(end, contentLength - 1);
        return new WebSeedRangeSelection(start, end - start + 1, Partial: true, Satisfiable: true);
    }

    static bool IsContentAccessFailure(Exception ex)
        => ex is ContentSourceException or IOException or UnauthorizedAccessException;
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

    public static WebSeedOpenResult Unavailable(string error)
        => new(WebSeedOpenStatus.Unavailable, null, 0, 0, 0, false, error);

    public static WebSeedOpenResult RangeNotSatisfiable(long contentLength)
        => new(WebSeedOpenStatus.RangeNotSatisfiable, null, 0, 0, contentLength, false, null);
}

public enum WebSeedOpenStatus
{
    Opened,
    NotFound,
    Unavailable,
    RangeNotSatisfiable
}

readonly record struct WebSeedRangeSelection(
    long Offset,
    long Length,
    bool Partial,
    bool Satisfiable)
{
    public static WebSeedRangeSelection NotSatisfiable => new(0, 0, Partial: false, Satisfiable: false);
}
