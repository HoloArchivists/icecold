using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Tracker;

public sealed class InMemoryTrackerPeerStore(IOptions<IcecoldOptions> options) : ITrackerPeerStore
{
    readonly object gate = new();
    readonly Dictionary<string, Dictionary<string, PeerEntry>> torrents = new(StringComparer.Ordinal);

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

            if (string.Equals(input.Event, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                peers.Remove(peerKey);
            }
            else
            {
                peers[peerKey] = new PeerEntry(
                    input.PeerId,
                    input.IpAddress,
                    input.Port,
                    input.Uploaded,
                    input.Downloaded,
                    input.Left,
                    now);
            }

            var complete = peers.Values.Count(p => p.Left == 0);
            var incomplete = peers.Count - complete;
            var selected = peers
                .Where(p => p.Key != peerKey)
                .OrderByDescending(p => p.Value.LastAnnouncedAt)
                .Take(maxPeers)
                .Select(p => new PeerSnapshot(p.Value.PeerId, p.Value.IpAddress, p.Value.Port, p.Value.Left))
                .ToArray();

            return new TrackerAnnounceResult(selected, complete, incomplete, interval, minInterval);
        }
    }

    static void Expire(Dictionary<string, PeerEntry> peers, DateTimeOffset now, TimeSpan timeout)
    {
        foreach (var key in peers.Where(p => now - p.Value.LastAnnouncedAt > timeout).Select(p => p.Key).ToArray())
            peers.Remove(key);
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
