using System.Net;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Tracker;

public sealed class InMemoryTrackerPeerStore(
    IOptions<IcecoldOptions> options,
    PeerWireAdvertisedPeerProvider advertisedPeerProvider) : ITrackerPeerStore
{
    readonly object gate = new();
    readonly Dictionary<string, Dictionary<string, PeerEntry>> torrents = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> completedCounts = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<DateTimeOffset>> announceRateLimits = new(StringComparer.Ordinal);

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
        var isStopped = string.Equals(input.Event, "stopped", StringComparison.OrdinalIgnoreCase);

        if (!trackerOptions.TrackPublicPeers)
            return AnnounceWithoutPublicPeers(maxPeers, interval, minInterval);

        lock (gate)
        {
            if (torrents.TryGetValue(input.InfoHashHex, out var peers))
            {
                Expire(peers, now, timeout);
                if (peers.Count == 0)
                {
                    torrents.Remove(input.InfoHashHex);
                    peers = null;
                }
            }

            var rateLimited = !isStopped && IsRateLimited(input.SourceIpAddress, now, trackerOptions.MaxAnnouncesPerSourceIpPerMinute);
            if (isStopped)
            {
                peers?.Remove(peerKey);
            }
            else if (!rateLimited)
            {
                if (peers is null)
                {
                    peers = new Dictionary<string, PeerEntry>(StringComparer.Ordinal);
                    torrents[input.InfoHashHex] = peers;
                }

                var hasPreviousPeer = peers.TryGetValue(peerKey, out var previous);
                var previousPeer = hasPreviousPeer ? previous : null;
                var hadIncompletePeer = previousPeer is { Left: > 0 };
                if (string.Equals(input.Event, "completed", StringComparison.OrdinalIgnoreCase) && (!hasPreviousPeer || hadIncompletePeer))
                    completedCounts[input.InfoHashHex] = completedCounts.GetValueOrDefault(input.InfoHashHex) + 1;

                var firstSeenCompleteAt = input.Left == 0
                    ? previousPeer is { Left: 0, FirstSeenCompleteAt: { } previousFirstSeenCompleteAt } ? previousFirstSeenCompleteAt : now
                    : (DateTimeOffset?)null;

                peers[peerKey] = new PeerEntry(
                    input.PeerId,
                    Normalize(input.IpAddress),
                    Normalize(input.SourceIpAddress),
                    input.Port,
                    input.Uploaded,
                    input.Downloaded,
                    input.Left,
                    firstSeenCompleteAt,
                    now);

                EvictOverLimit(peers, trackerOptions.MaxPeersStoredPerTorrent);
            }

            peers ??= [];

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

            if (peers.Count == 0)
                torrents.Remove(input.InfoHashHex);

            return new TrackerAnnounceResult(selected, complete, externalSwarm.Leechers, interval, minInterval);
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

        lock (gate)
        {
            if (!torrents.TryGetValue(infoHashHex, out var peers))
                peers = [];
            else
            {
                Expire(peers, now, timeout);
                if (peers.Count == 0)
                    torrents.Remove(infoHashHex);
            }

            var externalSwarm = GetSwarmStats(peers, now, standbyOptions);
            var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(externalSwarm, out _);
            var complete = externalSwarm.Seeders + (hasAdvertisedPeer ? 1 : 0);
            return new TrackerScrapeStats(complete, externalSwarm.Leechers, completedCounts.GetValueOrDefault(infoHashHex));
        }
    }

    public TrackerSwarmStats GetSwarmStats(string infoHashHex)
    {
        var trackerOptions = options.Value.Tracker;
        var standbyOptions = options.Value.PeerWire.Standby;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);
        var now = DateTimeOffset.UtcNow;

        if (!trackerOptions.TrackPublicPeers)
            return new TrackerSwarmStats(0, 0, 0);

        lock (gate)
        {
            if (!torrents.TryGetValue(infoHashHex, out var peers))
                return new TrackerSwarmStats(0, 0, 0);

            Expire(peers, now, timeout);
            if (peers.Count == 0)
            {
                torrents.Remove(infoHashHex);
                return new TrackerSwarmStats(0, 0, 0);
            }

            return GetSwarmStats(peers, now, standbyOptions);
        }
    }

    public TrackerPeerPruneResult PruneExpired(DateTimeOffset now)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.Tracker.PeerTimeoutSeconds);
        var removedPeers = 0;
        var removedTorrents = 0;

        lock (gate)
        {
            PruneAnnounceRateLimits(now);

            foreach (var (infoHash, peers) in torrents.ToArray())
            {
                removedPeers += Expire(peers, now, timeout);
                if (peers.Count > 0)
                    continue;

                torrents.Remove(infoHash);
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

        lock (gate)
        {
            foreach (var (infoHash, peers) in torrents.ToArray())
            {
                Expire(peers, now, timeout);
                if (peers.Count == 0)
                    torrents.Remove(infoHash);
            }

            var peerEntries = torrents.Values.SelectMany(p => p.Values).ToList();
            return new TrackerPeerStoreStats(
                ActiveInfoHashes: torrents.Count,
                Peers: peerEntries.Count,
                Seeders: peerEntries.LongCount(p => p.Left == 0),
                Leechers: peerEntries.LongCount(p => p.Left > 0),
                CompletedAnnounces: completedCounts.Values.Sum());
        }
    }

    static int Expire(Dictionary<string, PeerEntry> peers, DateTimeOffset now, TimeSpan timeout)
    {
        var expired = peers
            .Where(p => now - p.Value.LastAnnouncedAt > timeout)
            .Select(p => p.Key)
            .ToArray();

        foreach (var key in expired)
            peers.Remove(key);

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

    static void EvictOverLimit(Dictionary<string, PeerEntry> peers, int maxPeers)
    {
        if (peers.Count <= maxPeers)
            return;

        foreach (var key in peers
            .OrderBy(p => p.Value.LastAnnouncedAt)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(peers.Count - maxPeers)
            .Select(p => p.Key)
            .ToArray())
        {
            peers.Remove(key);
        }
    }

    bool IsRateLimited(IPAddress sourceIpAddress, DateTimeOffset now, int maxAnnouncesPerMinute)
    {
        if (maxAnnouncesPerMinute <= 0)
            return false;

        var key = Normalize(sourceIpAddress).ToString();
        if (!announceRateLimits.TryGetValue(key, out var timestamps))
        {
            timestamps = new Queue<DateTimeOffset>();
            announceRateLimits[key] = timestamps;
        }

        PruneAnnounceTimestamps(timestamps, now);
        if (timestamps.Count >= maxAnnouncesPerMinute)
            return true;

        timestamps.Enqueue(now);
        return false;
    }

    void PruneAnnounceRateLimits(DateTimeOffset now)
    {
        foreach (var (key, timestamps) in announceRateLimits.ToArray())
        {
            PruneAnnounceTimestamps(timestamps, now);
            if (timestamps.Count == 0)
                announceRateLimits.Remove(key);
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

    static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    sealed record PeerEntry(
        byte[] PeerId,
        IPAddress IpAddress,
        IPAddress SourceIpAddress,
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
