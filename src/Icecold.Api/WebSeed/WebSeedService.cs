using Icecold.Api.Content;
using Icecold.Api.Torrents;
using Microsoft.Net.Http.Headers;

namespace Icecold.Api.WebSeed;

public sealed class WebSeedService(TorrentLocationService locations)
{
    public async Task<WebSeedOpenResult> OpenAsync(
        string infoHash,
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
                    return WebSeedOpenResult.Conflict("No readable backing location is currently available for this torrent.");
                }

                return WebSeedOpenResult.NotFound();
            }

            var (offset, length, partial) = ParseRange(rangeHeader, resolved.Metadata.Length);
            try
            {
                var stream = partial
                    ? await resolved.Source.OpenRangeAsync(resolved.Metadata.Path, offset, length, cancellationToken)
                    : await resolved.Source.OpenReadAsync(resolved.Metadata.Path, cancellationToken);

                return WebSeedOpenResult.Opened(stream, offset, length, resolved.Metadata.Length, partial);
            }
            catch (Exception ex) when (IsContentAccessFailure(ex))
            {
                attemptedLocations.Add(resolved.Location.Id);
                await locations.ReportLocationFailureAsync(resolved.Location.Id, ex, cancellationToken);
            }
        }
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

    public static WebSeedOpenResult Conflict(string error)
        => new(WebSeedOpenStatus.Conflict, null, 0, 0, 0, false, error);
}

public enum WebSeedOpenStatus
{
    Opened,
    NotFound,
    Conflict
}
