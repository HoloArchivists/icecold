using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Tracker;

public sealed class InMemoryTrackerPeerStore(
    IOptions<IcecoldOptions> options,
    PeerWireAdvertisedPeerProvider advertisedPeerProvider) : ITrackerPeerStore
{
    // Request-path expiry is throttled per infohash; the background pruner and GetStats always do a full pass.
    static readonly TimeSpan ExpireThrottle = TimeSpan.FromSeconds(5);

    // Backstop for rate-limit state growth between prunes. Over this, expired entries are pruned inline and,
    // if every entry is still fresh, new sources are not tracked until the window rolls over.
    const int MaxRateLimitEntriesPerTorrent = 16384;

    // Each infohash has its own lock so announces for unrelated torrents never contend.
    readonly ConcurrentDictionary<string, TorrentBucket> torrents = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, long> completedCounts = new(StringComparer.Ordinal);

    public TrackerAnnounceResult Announce(TrackerAnnounceInput input)
    {
        var trackerOptions = options.Value.Tracker;
        var standbyOptions = options.Value.PeerWire.Standby;
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(trackerOptions.AnnounceIntervalSeconds);
        var minInterval = TimeSpan.FromSeconds(trackerOptions.MinAnnounceIntervalSeconds);
        var maxPeers = Math.Clamp(input.NumberWanted, 0, trackerOptions.MaxPeersReturned);
        var peerKey = BuildPeerKey(input);
        var sourceKey = BuildSourceKey(input.IpAddress);
        var isStopped = string.Equals(input.Event, "stopped", StringComparison.OrdinalIgnoreCase);

        if (!trackerOptions.TrackPublicPeers)
            return AnnounceWithoutPublicPeers(maxPeers, interval, minInterval);

        while (true)
        {
            var bucket = torrents.GetOrAdd(input.InfoHashHex, _ => new TorrentBucket());
            lock (bucket.Gate)
            {
                // Lost a race with empty-bucket removal; retry against the replacement bucket.
                if (bucket.Removed)
                    continue;

                ExpireThrottled(bucket, now, timeout);

                var rateLimited = !isStopped && IsRateLimited(bucket, sourceKey, now, trackerOptions.MaxAnnouncesPerSourceIpPerMinute);
                if (isStopped)
                {
                    bucket.Remove(peerKey);
                }
                else if (!rateLimited)
                {
                    var hasPreviousPeer = bucket.Peers.TryGetValue(peerKey, out var previous);
                    var previousPeer = hasPreviousPeer ? previous : null;
                    var hadIncompletePeer = previousPeer is { Left: > 0 };
                    if (string.Equals(input.Event, "completed", StringComparison.OrdinalIgnoreCase) && (!hasPreviousPeer || hadIncompletePeer))
                        completedCounts.AddOrUpdate(input.InfoHashHex, 1, (_, count) => count + 1);

                    var firstSeenCompleteAt = input.Left == 0
                        ? previousPeer is { Left: 0, FirstSeenCompleteAt: { } previousFirstSeenCompleteAt } ? previousFirstSeenCompleteAt : now
                        : (DateTimeOffset?)null;

                    bucket.Set(peerKey, new PeerEntry(
                        input.PeerId,
                        Normalize(input.IpAddress),
                        Normalize(input.SourceIpAddress),
                        sourceKey,
                        input.Port,
                        input.Uploaded,
                        input.Downloaded,
                        input.Left,
                        firstSeenCompleteAt,
                        now));

                    if (!hasPreviousPeer)
                        EvictSourceOverLimit(bucket, sourceKey, peerKey, trackerOptions.MaxPeersPerSourceIpPerTorrent);

                    EvictOverLimit(bucket, trackerOptions.MaxPeersStoredPerTorrent);
                }

                var peers = bucket.Peers;
                var externalSwarm = GetSwarmStats(peers, now, standbyOptions);
                var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(externalSwarm, out var advertisedPeer);
                var complete = externalSwarm.Seeders + (hasAdvertisedPeer ? 1 : 0);
                var selected = new List<PeerSnapshot>(Math.Min(maxPeers, peers.Count + 1));
                if (hasAdvertisedPeer && maxPeers > 0)
                    selected.Add(advertisedPeer);

                selected.AddRange(peers
                    .Where(p => p.Key != peerKey)
                    .OrderByDescending(p => p.Value.LastAnnouncedAt)
                    .Take(maxPeers - selected.Count)
                    .Select(p => new PeerSnapshot(p.Value.PeerId, p.Value.IpAddress, p.Value.Port, p.Value.Left)));

                RemoveIfEmpty(input.InfoHashHex, bucket);

                // A rate-limited announce was not stored, so ask the client to come back at the minimum interval
                // instead of disappearing from the swarm for a full announce interval.
                return new TrackerAnnounceResult(
                    selected,
                    complete,
                    externalSwarm.Leechers,
                    rateLimited ? minInterval : interval,
                    minInterval);
            }
        }
    }

    public TrackerScrapeStats Scrape(string infoHashHex)
    {
        var trackerOptions = options.Value.Tracker;
        var standbyOptions = options.Value.PeerWire.Standby;
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);

        if (!trackerOptions.TrackPublicPeers)
            return ScrapeWithoutPublicPeers();

        var externalSwarm = ReadSwarmStats(infoHashHex, now, timeout, standbyOptions);
        var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(externalSwarm, out _);
        var complete = externalSwarm.Seeders + (hasAdvertisedPeer ? 1 : 0);
        return new TrackerScrapeStats(complete, externalSwarm.Leechers, completedCounts.GetValueOrDefault(infoHashHex));
    }

    public TrackerSwarmStats GetSwarmStats(string infoHashHex)
    {
        var trackerOptions = options.Value.Tracker;
        var standbyOptions = options.Value.PeerWire.Standby;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);
        var now = DateTimeOffset.UtcNow;

        if (!trackerOptions.TrackPublicPeers)
            return new TrackerSwarmStats(0, 0, 0);

        return ReadSwarmStats(infoHashHex, now, timeout, standbyOptions);
    }

    public TrackerPeerPruneResult PruneExpired(DateTimeOffset now)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.Tracker.PeerTimeoutSeconds);
        var removedPeers = 0;
        var removedTorrents = 0;

        foreach (var (infoHash, bucket) in torrents)
        {
            lock (bucket.Gate)
            {
                if (bucket.Removed)
                    continue;

                PruneAnnounceRateLimits(bucket, now);
                removedPeers += Expire(bucket, now, timeout);
                if (RemoveIfEmpty(infoHash, bucket))
                    removedTorrents++;
            }
        }

        return new TrackerPeerPruneResult(removedPeers, removedTorrents);
    }

    public TrackerPeerStoreStats GetStats()
    {
        var trackerOptions = options.Value.Tracker;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);
        var now = DateTimeOffset.UtcNow;

        if (!trackerOptions.TrackPublicPeers)
            return new TrackerPeerStoreStats(0, 0, 0, 0, 0);

        long activeInfoHashes = 0;
        long peers = 0;
        long seeders = 0;
        foreach (var (infoHash, bucket) in torrents)
        {
            lock (bucket.Gate)
            {
                if (bucket.Removed)
                    continue;

                Expire(bucket, now, timeout);
                if (bucket.Peers.Count == 0)
                {
                    RemoveIfEmpty(infoHash, bucket);
                    continue;
                }

                activeInfoHashes++;
                peers += bucket.Peers.Count;
                seeders += bucket.Peers.Values.LongCount(p => p.Left == 0);
            }
        }

        return new TrackerPeerStoreStats(
            ActiveInfoHashes: activeInfoHashes,
            Peers: peers,
            Seeders: seeders,
            Leechers: peers - seeders,
            CompletedAnnounces: completedCounts.Values.Sum());
    }

    TrackerSwarmStats ReadSwarmStats(
        string infoHashHex,
        DateTimeOffset now,
        TimeSpan timeout,
        PeerWireStandbyOptions standbyOptions)
    {
        if (!torrents.TryGetValue(infoHashHex, out var bucket))
            return new TrackerSwarmStats(0, 0, 0);

        lock (bucket.Gate)
        {
            // A removed bucket was empty when it was removed.
            if (bucket.Removed)
                return new TrackerSwarmStats(0, 0, 0);

            ExpireThrottled(bucket, now, timeout);
            var stats = GetSwarmStats(bucket.Peers, now, standbyOptions);
            RemoveIfEmpty(infoHashHex, bucket);
            return stats;
        }
    }

    // Must be called while holding bucket.Gate. Rate-limit state goes with the bucket: a source can only empty a
    // bucket when it is the only participant, so dropping its counters protects nobody else.
    bool RemoveIfEmpty(string infoHashHex, TorrentBucket bucket)
    {
        if (bucket.Peers.Count > 0)
            return false;

        bucket.Removed = true;
        torrents.TryRemove(new KeyValuePair<string, TorrentBucket>(infoHashHex, bucket));
        return true;
    }

    static void ExpireThrottled(TorrentBucket bucket, DateTimeOffset now, TimeSpan timeout)
    {
        if (now - bucket.LastExpiredAt < ExpireThrottle)
            return;

        Expire(bucket, now, timeout);
        bucket.LastExpiredAt = now;
    }

    static int Expire(TorrentBucket bucket, DateTimeOffset now, TimeSpan timeout)
    {
        var expired = bucket.Peers
            .Where(p => now - p.Value.LastAnnouncedAt > timeout)
            .Select(p => p.Key)
            .ToArray();

        foreach (var key in expired)
            bucket.Remove(key);

        return expired.Length;
    }

    TrackerAnnounceResult AnnounceWithoutPublicPeers(int maxPeers, TimeSpan interval, TimeSpan minInterval)
    {
        var externalSwarm = new TrackerSwarmStats(0, 0, 0);
        var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(externalSwarm, out var advertisedPeer);
        IReadOnlyList<PeerSnapshot> peers = hasAdvertisedPeer && maxPeers > 0 ? [advertisedPeer] : [];
        return new TrackerAnnounceResult(peers, hasAdvertisedPeer ? 1 : 0, 0, interval, minInterval);
    }

    TrackerScrapeStats ScrapeWithoutPublicPeers()
    {
        var externalSwarm = new TrackerSwarmStats(0, 0, 0);
        var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(externalSwarm, out _);
        return new TrackerScrapeStats(hasAdvertisedPeer ? 1 : 0, 0, 0);
    }

    // Caps how many peers one client address can hold per infohash so a single host cannot flush real peers
    // out of the per-torrent LRU by minting peer_id/port combinations. The source's own oldest entry goes first.
    static void EvictSourceOverLimit(TorrentBucket bucket, string sourceKey, string currentPeerKey, int maxPeersPerSource)
    {
        var count = bucket.SourcePeerCounts.GetValueOrDefault(sourceKey);
        if (count <= maxPeersPerSource)
            return;

        foreach (var key in bucket.Peers
            .Where(p => p.Value.SourceKey == sourceKey && p.Key != currentPeerKey)
            .OrderBy(p => p.Value.LastAnnouncedAt)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(count - maxPeersPerSource)
            .Select(p => p.Key)
            .ToArray())
        {
            bucket.Remove(key);
        }
    }

    static void EvictOverLimit(TorrentBucket bucket, int maxPeers)
    {
        if (bucket.Peers.Count <= maxPeers)
            return;

        foreach (var key in bucket.Peers
            .OrderBy(p => p.Value.LastAnnouncedAt)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(bucket.Peers.Count - maxPeers)
            .Select(p => p.Key)
            .ToArray())
        {
            bucket.Remove(key);
        }
    }

    // Limits are per (client address, infohash): a legitimate client announces each torrent about once per
    // interval, so a busy mirror or a CGNAT address seeding many torrents is not throttled across all of them.
    static bool IsRateLimited(TorrentBucket bucket, string sourceKey, DateTimeOffset now, int maxAnnouncesPerMinute)
    {
        if (maxAnnouncesPerMinute <= 0)
            return false;

        var rateLimits = bucket.AnnounceRateLimits;
        if (!rateLimits.TryGetValue(sourceKey, out var timestamps))
        {
            if (rateLimits.Count >= MaxRateLimitEntriesPerTorrent)
            {
                PruneAnnounceRateLimits(bucket, now);
                if (rateLimits.Count >= MaxRateLimitEntriesPerTorrent)
                    return false;
            }

            timestamps = new Queue<DateTimeOffset>();
            rateLimits[sourceKey] = timestamps;
        }

        PruneAnnounceTimestamps(timestamps, now);
        if (timestamps.Count >= maxAnnouncesPerMinute)
            return true;

        timestamps.Enqueue(now);
        return false;
    }

    static void PruneAnnounceRateLimits(TorrentBucket bucket, DateTimeOffset now)
    {
        foreach (var (key, timestamps) in bucket.AnnounceRateLimits.ToArray())
        {
            PruneAnnounceTimestamps(timestamps, now);
            if (timestamps.Count == 0)
                bucket.AnnounceRateLimits.Remove(key);
        }
    }

    static void PruneAnnounceTimestamps(Queue<DateTimeOffset> timestamps, DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-1);
        while (timestamps.Count > 0 && timestamps.Peek() <= cutoff)
            timestamps.Dequeue();
    }

    static TrackerSwarmStats GetSwarmStats(
        Dictionary<string, PeerEntry> peers,
        DateTimeOffset now,
        PeerWireStandbyOptions standbyOptions)
    {
        var seeders = peers.Values.Count(p => p.Left == 0);
        var minimumCompleteAge = TimeSpan.FromSeconds(standbyOptions.MinimumExternalSeederAgeSeconds);
        var standbyEligibleSeeders = peers.Values
            .Where(p => p.Left == 0
                && p.FirstSeenCompleteAt is { } firstSeenCompleteAt
                && now - firstSeenCompleteAt >= minimumCompleteAge)
            .Select(p => Normalize(p.SourceIpAddress).ToString())
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TrackerSwarmStats(seeders, peers.Count - seeders, standbyEligibleSeeders);
    }

    static string BuildPeerKey(TrackerAnnounceInput input)
        => $"{InfoHashUtil.ToHex(input.PeerId)}@{Normalize(input.IpAddress)}:{input.Port}";

    // Client address used for per-source peer caps and rate limiting. This is the HTTP source IP unless the source
    // is trusted to supply ip=. IPv6 is grouped by /64 because a single host or subscriber usually controls a whole /64.
    static string BuildSourceKey(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    sealed class TorrentBucket
    {
        public object Gate { get; } = new();

        public Dictionary<string, PeerEntry> Peers { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> SourcePeerCounts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Queue<DateTimeOffset>> AnnounceRateLimits { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastExpiredAt { get; set; } = DateTimeOffset.MinValue;

        public bool Removed { get; set; }

        public void Set(string key, PeerEntry entry)
        {
            Remove(key);
            Peers[key] = entry;
            SourcePeerCounts[entry.SourceKey] = SourcePeerCounts.GetValueOrDefault(entry.SourceKey) + 1;
        }

        public void Remove(string key)
        {
            if (!Peers.Remove(key, out var entry))
                return;

            var count = SourcePeerCounts.GetValueOrDefault(entry.SourceKey) - 1;
            if (count > 0)
                SourcePeerCounts[entry.SourceKey] = count;
            else
                SourcePeerCounts.Remove(entry.SourceKey);
        }
    }

    sealed record PeerEntry(
        byte[] PeerId,
        IPAddress IpAddress,
        IPAddress SourceIpAddress,
        string SourceKey,
        int Port,
        long Uploaded,
        long Downloaded,
        long Left,
        DateTimeOffset? FirstSeenCompleteAt,
        DateTimeOffset LastAnnouncedAt);
}

public sealed record TrackerPeerPruneResult(int RemovedPeers, int RemovedTorrents);

public sealed record TrackerPeerStoreStats(
    long ActiveInfoHashes,
    long Peers,
    long Seeders,
    long Leechers,
    long CompletedAnnounces);
