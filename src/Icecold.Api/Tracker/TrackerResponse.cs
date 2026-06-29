using System.Net.Sockets;
using Icecold.Api.Torrents;

namespace Icecold.Api.Tracker;

public static class TrackerResponse
{
    public static byte[] Failure(string reason)
        => Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["failure reason"] = reason
        });

    public static byte[] Success(TrackerAnnounceResult result, bool compact)
    {
        var response = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["complete"] = result.Complete,
            ["incomplete"] = result.Incomplete,
            ["interval"] = (long)result.Interval.TotalSeconds,
            ["min interval"] = (long)result.MinInterval.TotalSeconds
        };

        if (compact)
        {
            response["peers"] = BuildCompactPeers(result.Peers, AddressFamily.InterNetwork);
            var peers6 = BuildCompactPeers(result.Peers, AddressFamily.InterNetworkV6);
            if (peers6.Length > 0)
                response["peers6"] = peers6;
        }
        else
        {
            response["peers"] = result.Peers
                .Select(peer => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ip"] = peer.IpAddress.ToString(),
                    ["peer id"] = peer.PeerId,
                    ["port"] = peer.Port
                })
                .ToArray();
        }

        return Bencode.Encode(response);
    }

    static byte[] BuildCompactPeers(IReadOnlyList<PeerSnapshot> peers, AddressFamily addressFamily)
    {
        using var stream = new MemoryStream();
        foreach (var peer in peers.Where(p => p.IpAddress.AddressFamily == addressFamily))
        {
            stream.Write(peer.IpAddress.GetAddressBytes());
            stream.WriteByte((byte)(peer.Port >> 8));
            stream.WriteByte((byte)(peer.Port & 0xff));
        }

        return stream.ToArray();
    }
}
