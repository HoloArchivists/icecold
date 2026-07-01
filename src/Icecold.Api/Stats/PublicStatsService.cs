using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Tracker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Stats;

public sealed class PublicStatsService(
    IcecoldDbContext db,
    InMemoryTrackerPeerStore peerStore,
    IOptions<IcecoldOptions> options,
    IMemoryCache cache)
{
    const string CacheKey = "public-stats";

    public async Task<PublicStatsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var cacheSeconds = options.Value.Stats.CacheSeconds;
        if (cacheSeconds <= 0)
            return await BuildAsync(cacheSeconds, cancellationToken);

        return (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds);
            return await BuildAsync(cacheSeconds, cancellationToken);
        }))!;
    }

    async Task<PublicStatsResponse> BuildAsync(int cacheSeconds, CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var ready = await db.Torrents.AsNoTracking()
            .Where(t => t.Status == TorrentStatus.Ready)
            .GroupBy(_ => 1)
            .Select(g => new ReadyTorrentAggregate(
                g.LongCount(),
                g.Sum(t => t.ContentLength),
                g.Max(t => (long?)t.ContentLength)))
            .FirstOrDefaultAsync(cancellationToken) ?? new ReadyTorrentAggregate(0, 0, null);

        var seeding = await db.Torrents.AsNoTracking()
            .Where(t => t.Status == TorrentStatus.Ready
                && (t.Locations.Any(l => l.Status == TorrentLocationStatus.Active) || !t.Locations.Any()))
            .GroupBy(_ => 1)
            .Select(g => new CountAndBytes(g.LongCount(), g.Sum(t => t.ContentLength)))
            .FirstOrDefaultAsync(cancellationToken) ?? new CountAndBytes(0, 0);

        var tracker = peerStore.GetStats();
        var icecoldOptions = options.Value;

        return new PublicStatsResponse(
            generatedAt,
            cacheSeconds,
            new TorrentStats(
                Ready: ready.Count,
                Seeding: seeding.Count,
                ReadyBytes: ready.Bytes,
                SeedingBytes: seeding.Bytes,
                LargestReadyBytes: ready.LargestBytes),
            new TrackerStats(
                ActiveInfoHashes: tracker.ActiveInfoHashes,
                Peers: tracker.Peers,
                Seeders: tracker.Seeders,
                Leechers: tracker.Leechers,
                CompletedAnnounces: tracker.CompletedAnnounces),
            new ServingStats(
                WebSeedEnabled: icecoldOptions.WebSeed.Enabled,
                PeerWireEnabled: icecoldOptions.PeerWire.Enabled));
    }

    sealed record CountAndBytes(long Count, long Bytes);

    sealed record ReadyTorrentAggregate(long Count, long Bytes, long? LargestBytes);
}

public sealed record PublicStatsResponse(
    DateTimeOffset GeneratedAt,
    int CacheSeconds,
    TorrentStats Torrents,
    TrackerStats Tracker,
    ServingStats Serving);

public sealed record TorrentStats(
    long Ready,
    long Seeding,
    long ReadyBytes,
    long SeedingBytes,
    long? LargestReadyBytes);

public sealed record TrackerStats(
    long ActiveInfoHashes,
    long Peers,
    long Seeders,
    long Leechers,
    long CompletedAnnounces);

public sealed record ServingStats(
    bool WebSeedEnabled,
    bool PeerWireEnabled);
