using System.Net;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Tracker;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
        var store = CreateStore();
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
        var store = CreateStore();
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

    [Fact]
    public void Announce_Includes_Configured_Icecold_Peer_When_PeerWire_Is_Enabled()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881
            }
        });

        var result = store.Announce(new TrackerAnnounceInput(
            new string('a', 40),
            Enumerable.Repeat((byte)2, 20).ToArray(),
            IPAddress.Parse("127.0.0.2"),
            6882,
            0,
            0,
            10,
            null,
            true,
            50));

        var peer = Assert.Single(result.Peers);
        Assert.Equal(IPAddress.Parse("127.0.0.10"), peer.IpAddress);
        Assert.Equal(6881, peer.Port);
        Assert.Equal(0, peer.Left);
        Assert.Equal(1, result.Complete);
        Assert.Equal(1, result.Incomplete);
    }

    [Fact]
    public void Announce_Does_Not_Inject_Icecold_Peer_When_Numwant_Is_Zero()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881
            }
        });

        var result = store.Announce(new TrackerAnnounceInput(
            new string('a', 40),
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
        Assert.Equal(1, result.Complete);
        Assert.Equal(1, result.Incomplete);
    }

    [Fact]
    public async Task Tracker_Service_Rejects_Unregistered_Torrent_Before_Peer_Store()
    {
        var options = new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        await using var db = new IcecoldDbContext(options);
        var store = new RecordingPeerStore();
        var service = new TrackerAnnounceService(db, store);

        var result = await service.AnnounceAsync(new TrackerAnnounceInput(
            new string('a', 40),
            Enumerable.Repeat((byte)2, 20).ToArray(),
            IPAddress.Parse("127.0.0.2"),
            6882,
            0,
            0,
            10,
            null,
            true,
            50), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("unregistered torrent", result.FailureReason);
        Assert.False(store.WasCalled);
    }

    static string PercentEncode(IEnumerable<byte> bytes)
        => string.Concat(bytes.Select(b => "%" + b.ToString("x2")));

    static InMemoryTrackerPeerStore CreateStore(IcecoldOptions? options = null)
    {
        var configured = Options.Create(options ?? new IcecoldOptions());
        return new InMemoryTrackerPeerStore(
            configured,
            new PeerWireAdvertisedPeerProvider(configured, new PeerWirePeerIdentity()));
    }

    sealed class RecordingPeerStore : ITrackerPeerStore
    {
        public bool WasCalled { get; private set; }

        public TrackerAnnounceResult Announce(TrackerAnnounceInput input)
        {
            WasCalled = true;
            return new TrackerAnnounceResult([], 0, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        }
    }
}
