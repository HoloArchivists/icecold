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

    public static byte[] Scrape(TrackerScrapeResult result)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "d5:filesd");
        foreach (var file in result.Files.OrderBy(file => file.InfoHash, ByteArrayComparer.Instance))
        {
            WriteBytes(stream, file.InfoHash);
            WriteAscii(stream, "d8:complete");
            WriteInteger(stream, file.Stats.Complete);
            WriteAscii(stream, "10:downloaded");
            WriteInteger(stream, file.Stats.Downloaded);
            WriteAscii(stream, "10:incomplete");
            WriteInteger(stream, file.Stats.Incomplete);
            WriteAscii(stream, "e");
        }

        WriteAscii(stream, "ee");
        return stream.ToArray();
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

    static void WriteBytes(Stream stream, byte[] bytes)
    {
        WriteAscii(stream, bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stream.WriteByte((byte)':');
        stream.Write(bytes);
    }

    static void WriteInteger(Stream stream, long value)
    {
        stream.WriteByte((byte)'i');
        WriteAscii(stream, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stream.WriteByte((byte)'e');
    }

    static void WriteAscii(Stream stream, string text)
        => stream.Write(System.Text.Encoding.ASCII.GetBytes(text));

    sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                var compared = x[i].CompareTo(y[i]);
                if (compared != 0)
                    return compared;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
