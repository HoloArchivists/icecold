using Icecold.Api.Data;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Tracker;

public sealed class TrackerScrapeService(IcecoldDbContext db, ITrackerPeerStore peerStore)
{
    public async Task<TrackerScrapeServiceResult> ScrapeAsync(
        IReadOnlyList<byte[]> infoHashes,
        CancellationToken cancellationToken)
    {
        if (infoHashes.Count == 0)
            return TrackerScrapeServiceResult.Failure("info_hash is required");

        var requested = infoHashes
            .Select(hash => new RequestedInfoHash(hash, InfoHashUtil.ToHex(hash)))
            .DistinctBy(hash => hash.Hex)
            .ToArray();
        var requestedHexes = requested.Select(hash => hash.Hex).ToArray();

        var ready = await db.Torrents.AsNoTracking()
            .Where(t => t.InfoHashHex != null
                && t.Status == TorrentStatus.Ready
                && requestedHexes.Contains(t.InfoHashHex))
            .Select(t => t.InfoHashHex!)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var readySet = ready.ToHashSet(StringComparer.Ordinal);

        var files = requested
            .Where(hash => readySet.Contains(hash.Hex))
            .Select(hash => new TrackerScrapeEntry(hash.Bytes, peerStore.Scrape(hash.Hex)))
            .ToArray();

        return TrackerScrapeServiceResult.Success(new TrackerScrapeResult(files));
    }

    sealed record RequestedInfoHash(byte[] Bytes, string Hex);
}

public sealed record TrackerScrapeServiceResult(TrackerScrapeResult? Scrape, string? FailureReason)
{
    public bool Succeeded => Scrape is not null;

    public static TrackerScrapeServiceResult Success(TrackerScrapeResult scrape)
        => new(scrape, null);

    public static TrackerScrapeServiceResult Failure(string failureReason)
        => new(null, failureReason);
}
