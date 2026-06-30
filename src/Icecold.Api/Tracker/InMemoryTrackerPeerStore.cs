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

    public TrackerAnnounceResult Announce(TrackerAnnounceInput input)
    {
        var trackerOptions = options.Value.Tracker;
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);
        var interval = TimeSpan.FromSeconds(trackerOptions.AnnounceIntervalSeconds);
        var minInterval = TimeSpan.FromSeconds(trackerOptions.MinAnnounceIntervalSeconds);
        var maxPeers = Math.Clamp(input.NumberWanted, 0, trackerOptions.MaxPeersReturned);
        var peerKey = BuildPeerKey(input);

        lock (gate)
        {
            if (!torrents.TryGetValue(input.InfoHashHex, out var peers))
            {
                peers = new Dictionary<string, PeerEntry>(StringComparer.Ordinal);
                torrents[input.InfoHashHex] = peers;
            }

            Expire(peers, now, timeout);

            var hadIncompletePeer = peers.TryGetValue(peerKey, out var previous) && previous.Left > 0;
            if (string.Equals(input.Event, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                peers.Remove(peerKey);
            }
            else
            {
                if (string.Equals(input.Event, "completed", StringComparison.OrdinalIgnoreCase) && (!peers.ContainsKey(peerKey) || hadIncompletePeer))
                    completedCounts[input.InfoHashHex] = completedCounts.GetValueOrDefault(input.InfoHashHex) + 1;

                peers[peerKey] = new PeerEntry(
                    input.PeerId,
                    input.IpAddress,
                    input.Port,
                    input.Uploaded,
                    input.Downloaded,
                    input.Left,
                    now);
            }

            EvictOverLimit(peers, trackerOptions.MaxPeersStoredPerTorrent);

            var existingComplete = peers.Values.Count(p => p.Left == 0);
            var existingIncomplete = peers.Count - existingComplete;
            var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(out var advertisedPeer);
            var complete = existingComplete + (hasAdvertisedPeer ? 1 : 0);
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

            return new TrackerAnnounceResult(selected, complete, existingIncomplete, interval, minInterval);
        }
    }

    public TrackerScrapeStats Scrape(string infoHashHex)
    {
        var trackerOptions = options.Value.Tracker;
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(trackerOptions.PeerTimeoutSeconds);

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

            var existingComplete = peers.Values.Count(p => p.Left == 0);
            var existingIncomplete = peers.Count - existingComplete;
            var hasAdvertisedPeer = advertisedPeerProvider.TryGetPeer(out _);
            var complete = existingComplete + (hasAdvertisedPeer ? 1 : 0);
            return new TrackerScrapeStats(complete, existingIncomplete, completedCounts.GetValueOrDefault(infoHashHex));
        }
    }

    public TrackerPeerPruneResult PruneExpired(DateTimeOffset now)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.Tracker.PeerTimeoutSeconds);
        var removedPeers = 0;
        var removedTorrents = 0;

        lock (gate)
        {
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

    static string BuildPeerKey(TrackerAnnounceInput input)
        => $"{InfoHashUtil.ToHex(input.PeerId)}@{input.IpAddress}:{input.Port}";

    sealed record PeerEntry(
        byte[] PeerId,
        System.Net.IPAddress IpAddress,
        int Port,
        long Uploaded,
        long Downloaded,
        long Left,
        DateTimeOffset LastAnnouncedAt);
}

public sealed record TrackerPeerPruneResult(int RemovedPeers, int RemovedTorrents);
