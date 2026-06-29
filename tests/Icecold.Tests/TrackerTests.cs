using System.Net;
using Icecold.Api.Options;
using Icecold.Api.Tracker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class TrackerTests
{
    [Fact]
    public void TryParse_Preserves_Raw_InfoHash_Bytes()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var peerId = Enumerable.Range(20, 20).Select(i => (byte)i).ToArray();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Request.QueryString = new QueryString(
            "?info_hash=" + PercentEncode(infoHash)
            + "&peer_id=" + PercentEncode(peerId)
            + "&port=6881&uploaded=1&downloaded=2&left=3&compact=1&numwant=25&event=started");

        var parsed = TrackerQueryParser.TryParse(context.Request, out var input, out var failure);

        Assert.True(parsed, failure);
        Assert.NotNull(input);
        Assert.Equal("000102030405060708090a0b0c0d0e0f10111213", input.InfoHashHex);
        Assert.True(input.Compact);
        Assert.Equal(25, input.NumberWanted);
        Assert.Equal("started", input.Event);
    }

    [Fact]
    public void Announce_Tracks_Counts_And_Excludes_Current_Peer()
    {
        var store = new InMemoryTrackerPeerStore(Options.Create(new IcecoldOptions()));
        var infoHash = new string('a', 40);
        var firstPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var secondPeer = Enumerable.Repeat((byte)2, 20).ToArray();

        var first = store.Announce(new TrackerAnnounceInput(
            infoHash,
            firstPeer,
            IPAddress.Parse("127.0.0.1"),
            6881,
            0,
            0,
            10,
            "started",
            true,
            50));

        var second = store.Announce(new TrackerAnnounceInput(
            infoHash,
            secondPeer,
            IPAddress.Parse("127.0.0.2"),
            6882,
            0,
            10,
            0,
            "started",
            true,
            50));

        Assert.Empty(first.Peers);
        Assert.Equal(1, first.Incomplete);
        Assert.Single(second.Peers);
        Assert.Equal(firstPeer, second.Peers[0].PeerId);
        Assert.Equal(1, second.Complete);
        Assert.Equal(1, second.Incomplete);
    }

    [Fact]
    public void Announce_Honors_Explicit_Zero_Numwant()
    {
        var store = new InMemoryTrackerPeerStore(Options.Create(new IcecoldOptions()));
        var infoHash = new string('a', 40);

        _ = store.Announce(new TrackerAnnounceInput(
            infoHash,
            Enumerable.Repeat((byte)1, 20).ToArray(),
            IPAddress.Parse("127.0.0.1"),
            6881,
            0,
            0,
            10,
            "started",
            true,
            50));

        var result = store.Announce(new TrackerAnnounceInput(
            infoHash,
            Enumerable.Repeat((byte)2, 20).ToArray(),
            IPAddress.Parse("127.0.0.2"),
            6882,
            0,
            0,
            10,
            null,
            true,
            0));

        Assert.Empty(result.Peers);
        Assert.Equal(2, result.Incomplete);
    }

    static string PercentEncode(IEnumerable<byte> bytes)
        => string.Concat(bytes.Select(b => "%" + b.ToString("x2")));
}
