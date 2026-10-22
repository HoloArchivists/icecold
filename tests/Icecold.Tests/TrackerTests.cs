using System.Net;
using Icecold.Api.Controllers;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Tracker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        var parsed = TrackerQueryParser.TryParse(context.Request, new TrackerOptions(), out var input, out var failure);

        Assert.True(parsed, failure);
        Assert.NotNull(input);
        Assert.Equal("000102030405060708090a0b0c0d0e0f10111213", input.InfoHashHex);
        Assert.True(input.Compact);
        Assert.Equal(25, input.NumberWanted);
        Assert.Equal("started", input.Event);
    }

    [Fact]
    public void TryParse_Ignores_Announce_Ip_Parameter_By_Default()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var peerId = Enumerable.Range(20, 20).Select(i => (byte)i).ToArray();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Request.QueryString = new QueryString(
            "?info_hash=" + PercentEncode(infoHash)
            + "&peer_id=" + PercentEncode(peerId)
            + "&port=6881&uploaded=0&downloaded=0&left=0&ip=198.51.100.25");

        var parsed = TrackerQueryParser.TryParse(context.Request, new TrackerOptions(), out var input, out var failure);

        Assert.True(parsed, failure);
        Assert.NotNull(input);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), input.IpAddress);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), input.SourceIpAddress);
    }

    [Fact]
    public void TryParse_Honors_Announce_Ip_Parameter_From_Trusted_Source()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var peerId = Enumerable.Range(20, 20).Select(i => (byte)i).ToArray();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Request.QueryString = new QueryString(
            "?info_hash=" + PercentEncode(infoHash)
            + "&peer_id=" + PercentEncode(peerId)
            + "&port=6881&uploaded=0&downloaded=0&left=0&ip=198.51.100.25");
        var options = new TrackerOptions
        {
            TrustedIpParameterSources = ["127.0.0.0/24"]
        };

        var parsed = TrackerQueryParser.TryParse(context.Request, options, out var input, out var failure);

        Assert.True(parsed, failure);
        Assert.NotNull(input);
        Assert.Equal(IPAddress.Parse("198.51.100.25"), input.IpAddress);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), input.SourceIpAddress);
    }

    [Theory]
    [InlineData("uploaded")]
    [InlineData("downloaded")]
    [InlineData("left")]
    public void TryParse_Rejects_Negative_Transfer_Counters(string counter)
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var peerId = Enumerable.Range(20, 20).Select(i => (byte)i).ToArray();
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["uploaded"] = 0,
            ["downloaded"] = 0,
            ["left"] = 0
        };
        values[counter] = -1;
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        context.Request.QueryString = new QueryString(
            "?info_hash=" + PercentEncode(infoHash)
            + "&peer_id=" + PercentEncode(peerId)
            + "&port=6881"
            + "&uploaded=" + values["uploaded"]
            + "&downloaded=" + values["downloaded"]
            + "&left=" + values["left"]);

        var parsed = TrackerQueryParser.TryParse(context.Request, new TrackerOptions(), out var input, out var failure);

        Assert.False(parsed);
        Assert.Null(input);
        Assert.Equal("uploaded, downloaded, and left must be non-negative", failure);
    }

    [Fact]
    public void TryParseScrape_Preserves_Raw_InfoHash_Bytes()
    {
        var first = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var second = Enumerable.Range(20, 20).Select(i => (byte)i).ToArray();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(
            "?info_hash=" + PercentEncode(first)
            + "&info_hash=" + PercentEncode(second));

        var parsed = TrackerQueryParser.TryParseScrape(context.Request, out var infoHashes, out var failure);

        Assert.True(parsed, failure);
        Assert.Equal(2, infoHashes.Count);
        Assert.Equal(first, infoHashes[0]);
        Assert.Equal(second, infoHashes[1]);
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
    public void Announce_RateLimits_Source_Ip_State_Updates()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxAnnouncesPerSourceIpPerMinute = 1
            }
        });
        var infoHash = new string('a', 40);
        var firstPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var secondPeer = Enumerable.Repeat((byte)2, 20).ToArray();

        var first = store.Announce(CreateAnnounce(infoHash, firstPeer, "127.0.0.1", 6881, left: 0));
        var result = store.Announce(CreateAnnounce(infoHash, secondPeer, "127.0.0.1", 6882, left: 0));

        Assert.Equal(TimeSpan.FromSeconds(1800), first.Interval);
        var peer = Assert.Single(result.Peers);
        Assert.Equal(firstPeer, peer.PeerId);
        Assert.Equal(1, result.Complete);
        Assert.Equal(0, result.Incomplete);
        Assert.Equal(TimeSpan.FromSeconds(300), result.Interval);
        Assert.Equal(TimeSpan.FromSeconds(300), result.MinInterval);
        Assert.Equal(1, store.Scrape(infoHash).Complete);
    }

    [Fact]
    public void Announce_RateLimits_Per_Source_And_InfoHash()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxAnnouncesPerSourceIpPerMinute = 1
            }
        });
        var firstHash = new string('a', 40);
        var secondHash = new string('b', 40);
        var peerId = Enumerable.Repeat((byte)1, 20).ToArray();

        _ = store.Announce(CreateAnnounce(firstHash, peerId, "198.51.100.1", 6881));
        var other = store.Announce(CreateAnnounce(secondHash, peerId, "198.51.100.1", 6881));

        Assert.Equal(TimeSpan.FromSeconds(1800), other.Interval);
        Assert.Equal(1, store.Scrape(firstHash).Incomplete);
        Assert.Equal(1, store.Scrape(secondHash).Incomplete);
    }

    [Fact]
    public void Announce_RateLimits_Ipv6_Sources_By_Slash64()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxAnnouncesPerSourceIpPerMinute = 1
            }
        });
        var infoHash = new string('a', 40);

        _ = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)1, 20).ToArray(), "2001:db8:1:1::1", 6881));
        var samePrefix = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)2, 20).ToArray(), "2001:db8:1:1::2", 6882));
        var otherPrefix = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)3, 20).ToArray(), "2001:db8:1:2::1", 6883));

        Assert.Equal(TimeSpan.FromSeconds(300), samePrefix.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1800), otherPrefix.Interval);
        Assert.Equal(2, store.Scrape(infoHash).Incomplete);
    }

    [Fact]
    public void Announce_Caps_Peers_Per_Source_And_Evicts_That_Sources_Oldest_Entry()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxPeersPerSourceIpPerTorrent = 2,
                MaxPeersStoredPerTorrent = 4,
                MaxPeersReturned = 50,
                MaxAnnouncesPerSourceIpPerMinute = 0
            }
        });
        var infoHash = new string('a', 40);
        var honestPeer = Enumerable.Repeat((byte)200, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, honestPeer, "198.51.100.200", 6881));
        for (byte i = 1; i <= 10; i++)
        {
            Thread.Sleep(2);
            _ = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat(i, 20).ToArray(), "203.0.113.1", 7000 + i));
        }

        var result = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)100, 20).ToArray(), "192.0.2.1", 6900));

        Assert.Contains(result.Peers, p => p.PeerId.SequenceEqual(honestPeer));
        Assert.Contains(result.Peers, p => p.PeerId.SequenceEqual(Enumerable.Repeat((byte)10, 20)));
        Assert.DoesNotContain(result.Peers, p => p.PeerId.SequenceEqual(Enumerable.Repeat((byte)8, 20)));
        Assert.Equal(4, store.Scrape(infoHash).Incomplete);
        Assert.Equal(4, store.GetStats().Peers);
    }

    [Fact]
    public void Announce_Caps_Ipv6_Peers_Per_Slash64()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxPeersPerSourceIpPerTorrent = 1,
                MaxAnnouncesPerSourceIpPerMinute = 0
            }
        });
        var infoHash = new string('a', 40);

        _ = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)1, 20).ToArray(), "2001:db8:1:1::1", 6881));
        _ = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)2, 20).ToArray(), "2001:db8:1:1::2", 6882));
        _ = store.Announce(CreateAnnounce(infoHash, Enumerable.Repeat((byte)3, 20).ToArray(), "2001:db8:1:2::1", 6883));

        Assert.Equal(2, store.Scrape(infoHash).Incomplete);
    }

    [Fact]
    public void Announce_Reannounce_Does_Not_Count_Against_Per_Source_Cap()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxPeersPerSourceIpPerTorrent = 2,
                MaxAnnouncesPerSourceIpPerMinute = 0
            }
        });
        var infoHash = new string('a', 40);
        var first = Enumerable.Repeat((byte)1, 20).ToArray();
        var second = Enumerable.Repeat((byte)2, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, first, "203.0.113.1", 6881));
        _ = store.Announce(CreateAnnounce(infoHash, second, "203.0.113.1", 6882));
        for (var i = 0; i < 5; i++)
            _ = store.Announce(CreateAnnounce(infoHash, first, "203.0.113.1", 6881));

        Assert.Equal(2, store.Scrape(infoHash).Incomplete);
    }

    [Fact]
    public async Task Announce_Is_Consistent_Under_Concurrent_Load()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxAnnouncesPerSourceIpPerMinute = 0
            }
        });
        var infoHashes = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 40)).ToArray();

        await Parallel.ForAsync(0, 400, (i, cancellationToken) =>
        {
            var infoHash = infoHashes[i % infoHashes.Length];
            var peerId = BitConverter.GetBytes(i).Concat(new byte[16]).ToArray();
            _ = store.Announce(CreateAnnounce(infoHash, peerId, $"10.0.{i / 250}.{i % 250}", 6881, left: i % 2));
            _ = store.Scrape(infoHash);
            _ = store.GetSwarmStats(infoHash);
            if (i % 50 == 0)
                _ = store.GetStats();
            return ValueTask.CompletedTask;
        });

        var stats = store.GetStats();
        Assert.Equal(4, stats.ActiveInfoHashes);
        Assert.Equal(400, stats.Peers);
        Assert.Equal(200, stats.Seeders);
        Assert.Equal(200, stats.Leechers);
    }

    [Fact]
    public void Announce_Stopped_Removes_Peer_And_Empty_Bucket()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxAnnouncesPerSourceIpPerMinute = 0
            }
        });
        var infoHash = new string('a', 40);
        var peerId = Enumerable.Repeat((byte)1, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, peerId, "198.51.100.1", 6881));
        _ = store.Announce(CreateAnnounce(infoHash, peerId, "198.51.100.1", 6881) with { Event = "stopped" });

        Assert.Equal(0, store.Scrape(infoHash).Incomplete);
        Assert.Equal(0, store.GetStats().ActiveInfoHashes);

        _ = store.Announce(CreateAnnounce(infoHash, peerId, "198.51.100.1", 6881));
        Assert.Equal(1, store.Scrape(infoHash).Incomplete);
    }

    [Fact]
    public void Announce_Evicts_Least_Recently_Seen_Peers_Over_Per_Torrent_Limit()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                MaxPeersStoredPerTorrent = 2,
                MaxPeersReturned = 50
            }
        });
        var infoHash = new string('a', 40);
        var firstPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var secondPeer = Enumerable.Repeat((byte)2, 20).ToArray();
        var thirdPeer = Enumerable.Repeat((byte)3, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, firstPeer, "127.0.0.1", 6881));
        Thread.Sleep(5);
        _ = store.Announce(CreateAnnounce(infoHash, secondPeer, "127.0.0.2", 6882));
        Thread.Sleep(5);
        var result = store.Announce(CreateAnnounce(infoHash, thirdPeer, "127.0.0.3", 6883));

        Assert.Single(result.Peers);
        Assert.Equal(secondPeer, result.Peers[0].PeerId);
        Assert.Equal(0, result.Complete);
        Assert.Equal(2, result.Incomplete);
        Assert.Equal(2, store.Scrape(infoHash).Incomplete);
    }

    [Fact]
    public void PruneExpired_Removes_Expired_Peers_And_Empty_Buckets()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                PeerTimeoutSeconds = 1
            }
        });
        var firstHash = new string('a', 40);
        var secondHash = new string('b', 40);

        _ = store.Announce(CreateAnnounce(firstHash, Enumerable.Repeat((byte)1, 20).ToArray(), "127.0.0.1", 6881));
        _ = store.Announce(CreateAnnounce(secondHash, Enumerable.Repeat((byte)2, 20).ToArray(), "127.0.0.2", 6882));

        var result = store.PruneExpired(DateTimeOffset.UtcNow.AddSeconds(2));

        Assert.Equal(2, result.RemovedPeers);
        Assert.Equal(2, result.RemovedTorrents);
        Assert.Equal(0, store.Scrape(firstHash).Incomplete);
        Assert.Equal(0, store.Scrape(secondHash).Incomplete);
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
    public void Announce_Disables_Public_Peer_Tracking_When_Configured()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                TrackPublicPeers = false
            },
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881
            }
        });
        var infoHash = new string('a', 40);

        var first = store.Announce(CreateAnnounce(
            infoHash,
            Enumerable.Repeat((byte)1, 20).ToArray(),
            "127.0.0.1",
            6881,
            left: 0));
        var second = store.Announce(CreateAnnounce(
            infoHash,
            Enumerable.Repeat((byte)2, 20).ToArray(),
            "127.0.0.2",
            6882));

        var firstPeer = Assert.Single(first.Peers);
        Assert.Equal(IPAddress.Parse("127.0.0.10"), firstPeer.IpAddress);
        Assert.Equal(1, first.Complete);
        Assert.Equal(0, first.Incomplete);

        var secondPeer = Assert.Single(second.Peers);
        Assert.Equal(IPAddress.Parse("127.0.0.10"), secondPeer.IpAddress);
        Assert.Equal(1, second.Complete);
        Assert.Equal(0, second.Incomplete);

        var scrape = store.Scrape(infoHash);
        Assert.Equal(1, scrape.Complete);
        Assert.Equal(0, scrape.Incomplete);
        Assert.Equal(0, scrape.Downloaded);

        var swarm = store.GetSwarmStats(infoHash);
        Assert.Equal(0, swarm.Seeders);
        Assert.Equal(0, swarm.Leechers);
        Assert.Equal(0, swarm.StandbyEligibleSeeders);

        var stats = store.GetStats();
        Assert.Equal(0, stats.ActiveInfoHashes);
        Assert.Equal(0, stats.Peers);
        Assert.Equal(0, stats.Seeders);
        Assert.Equal(0, stats.Leechers);
        Assert.Equal(0, stats.CompletedAnnounces);
    }

    [Fact]
    public void Announce_Disabled_Public_Peer_Tracking_Still_Honors_Numwant_Zero()
    {
        var store = CreateStore(new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                TrackPublicPeers = false
            },
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
        Assert.Equal(0, result.Incomplete);
    }

    [Fact]
    public void Announce_Standby_Still_Includes_Icecold_When_Only_Incomplete_Peers_Exist()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881,
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 1,
                    MinimumExternalSeederAgeSeconds = 0
                }
            }
        });
        var infoHash = new string('a', 40);
        var firstPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var secondPeer = Enumerable.Repeat((byte)2, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, firstPeer, "127.0.0.1", 6881));
        var result = store.Announce(CreateAnnounce(infoHash, secondPeer, "127.0.0.2", 6882));

        Assert.Contains(result.Peers, p => p.IpAddress.Equals(IPAddress.Parse("127.0.0.10")));
        Assert.Equal(1, result.Complete);
        Assert.Equal(2, result.Incomplete);
    }

    [Fact]
    public void Announce_Standby_Excludes_Icecold_When_Enough_External_Seeders_Exist()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881,
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 1,
                    MinimumExternalSeederAgeSeconds = 0
                }
            }
        });
        var infoHash = new string('a', 40);
        var seedPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var leechPeer = Enumerable.Repeat((byte)2, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, seedPeer, "127.0.0.1", 6881, left: 0));
        var result = store.Announce(CreateAnnounce(infoHash, leechPeer, "127.0.0.2", 6882));

        Assert.DoesNotContain(result.Peers, p => p.IpAddress.Equals(IPAddress.Parse("127.0.0.10")));
        Assert.Single(result.Peers);
        Assert.Equal(seedPeer, result.Peers[0].PeerId);
        Assert.Equal(1, result.Complete);
        Assert.Equal(1, result.Incomplete);
    }

    [Fact]
    public void Scrape_Standby_Excludes_Icecold_When_Enough_External_Seeders_Exist()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881,
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 1,
                    MinimumExternalSeederAgeSeconds = 0
                }
            }
        });
        var infoHash = new string('a', 40);

        _ = store.Announce(CreateAnnounce(
            infoHash,
            Enumerable.Repeat((byte)1, 20).ToArray(),
            "127.0.0.1",
            6881,
            left: 0));

        var result = store.Scrape(infoHash);

        Assert.Equal(1, result.Complete);
        Assert.Equal(0, result.Incomplete);
    }

    [Fact]
    public void Announce_Standby_Includes_Icecold_When_External_Seeder_Is_Fresh()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881,
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 1,
                    MinimumExternalSeederAgeSeconds = 300
                }
            }
        });
        var infoHash = new string('a', 40);
        var seedPeer = Enumerable.Repeat((byte)1, 20).ToArray();
        var leechPeer = Enumerable.Repeat((byte)2, 20).ToArray();

        _ = store.Announce(CreateAnnounce(infoHash, seedPeer, "127.0.0.1", 6881, left: 0));
        var result = store.Announce(CreateAnnounce(infoHash, leechPeer, "127.0.0.2", 6882));

        Assert.Contains(result.Peers, p => p.IpAddress.Equals(IPAddress.Parse("127.0.0.10")));
        Assert.Equal(2, result.Complete);
        Assert.Equal(1, result.Incomplete);
    }

    [Fact]
    public void Announce_Standby_Counts_Distinct_Source_Ips()
    {
        var store = CreateStore(new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881,
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 2,
                    MinimumExternalSeederAgeSeconds = 0
                }
            }
        });
        var infoHash = new string('a', 40);
        var firstSeed = CreateAnnounce(infoHash, Enumerable.Repeat((byte)1, 20).ToArray(), "203.0.113.1", 6881, left: 0)
            with { SourceIpAddress = IPAddress.Parse("198.51.100.1") };
        var secondSeed = CreateAnnounce(infoHash, Enumerable.Repeat((byte)2, 20).ToArray(), "203.0.113.2", 6882, left: 0)
            with { SourceIpAddress = IPAddress.Parse("198.51.100.1") };
        var leecher = CreateAnnounce(infoHash, Enumerable.Repeat((byte)3, 20).ToArray(), "198.51.100.2", 6883);

        _ = store.Announce(firstSeed);
        _ = store.Announce(secondSeed);
        var result = store.Announce(leecher);

        Assert.Contains(result.Peers, p => p.IpAddress.Equals(IPAddress.Parse("127.0.0.10")));
        Assert.Equal(3, result.Complete);
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
    public void Scrape_Includes_Configured_Icecold_Seed_When_PeerWire_Is_Enabled()
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

        var result = store.Scrape(new string('a', 40));

        Assert.Equal(1, result.Complete);
        Assert.Equal(0, result.Incomplete);
        Assert.Equal(0, result.Downloaded);
    }

    [Fact]
    public void Scrape_Response_Uses_Raw_InfoHash_Key()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var response = TrackerResponse.Scrape(new TrackerScrapeResult(
        [
            new TrackerScrapeEntry(infoHash, new TrackerScrapeStats(1, 2, 3))
        ]));

        var expected = new byte[]
            {
                (byte)'d', (byte)'5', (byte)':', (byte)'f', (byte)'i', (byte)'l', (byte)'e', (byte)'s', (byte)'d',
                (byte)'2', (byte)'0', (byte)':'
            }
            .Concat(infoHash)
            .Concat("d8:completei1e10:downloadedi3e10:incompletei2eeee"u8.ToArray())
            .ToArray();
        Assert.Equal(expected, response);
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

    [Fact]
    public async Task Announce_Endpoint_Returns_NotFound_When_Tracker_Is_Disabled()
    {
        await using var db = CreateTrackerDb();
        var store = new RecordingPeerStore();
        var controller = CreateTrackerController(db, store, trackerEnabled: false);

        var result = await controller.Announce(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(store.WasCalled);
    }

    [Fact]
    public async Task Scrape_Endpoint_Returns_NotFound_When_Tracker_Is_Disabled()
    {
        await using var db = CreateTrackerDb();
        var store = new RecordingPeerStore();
        var controller = CreateTrackerController(db, store, trackerEnabled: false);

        var result = await controller.Scrape(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(store.WasCalled);
    }

    [Fact]
    public void TrackerOptionsValidator_Rejects_Invalid_Prune_Settings()
    {
        var result = new TrackerOptionsValidator().Validate(null, new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                AnnounceIntervalSeconds = 0,
                MinAnnounceIntervalSeconds = 0,
                PeerTimeoutSeconds = 0,
                MaxPeersReturned = -1,
                MaxPeersStoredPerTorrent = 0,
                PruneIntervalSeconds = 0,
                MaxAnnouncesPerSourceIpPerMinute = -1,
                AdditionalAnnounceUrls =
                [
                    "",
                    "ftp://tracker.example.test/announce"
                ],
                TrustedIpParameterSources =
                [
                    "",
                    "not-a-network"
                ]
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:Tracker:AnnounceIntervalSeconds must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Tracker:MinAnnounceIntervalSeconds must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Tracker:PeerTimeoutSeconds must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Tracker:MaxPeersReturned must be at least 0.", result.Failures);
        Assert.Contains("Icecold:Tracker:MaxPeersStoredPerTorrent must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Tracker:PruneIntervalSeconds must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Tracker:MaxAnnouncesPerSourceIpPerMinute must be at least 0.", result.Failures);
        Assert.Contains("Icecold:Tracker:AdditionalAnnounceUrls entries must not be blank.", result.Failures);
        Assert.Contains("Icecold:Tracker:AdditionalAnnounceUrls entries must be absolute http, https, or udp tracker URLs.", result.Failures);
        Assert.Contains("Icecold:Tracker:TrustedIpParameterSources entries must not be blank.", result.Failures);
        Assert.Contains("Icecold:Tracker:TrustedIpParameterSources entries must be IP addresses or CIDR ranges.", result.Failures);
    }

    [Fact]
    public void TrackerOptionsValidator_Rejects_Inconsistent_Intervals_And_Source_Cap()
    {
        var result = new TrackerOptionsValidator().Validate(null, new IcecoldOptions
        {
            Tracker = new TrackerOptions
            {
                AnnounceIntervalSeconds = 600,
                MinAnnounceIntervalSeconds = 900,
                PeerTimeoutSeconds = 600,
                MaxPeersPerSourceIpPerTorrent = 0
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:Tracker:MinAnnounceIntervalSeconds must not exceed AnnounceIntervalSeconds.", result.Failures);
        Assert.Contains("Icecold:Tracker:PeerTimeoutSeconds must be greater than AnnounceIntervalSeconds.", result.Failures);
        Assert.Contains("Icecold:Tracker:MaxPeersPerSourceIpPerTorrent must be at least 1.", result.Failures);
        Assert.False(new TrackerOptionsValidator().Validate(null, new IcecoldOptions()).Failed);
    }

    static string PercentEncode(IEnumerable<byte> bytes)
        => string.Concat(bytes.Select(b => "%" + b.ToString("x2")));

    static TrackerAnnounceInput CreateAnnounce(
        string infoHash,
        byte[] peerId,
        string ipAddress,
        int port,
        long left = 10)
        => new(
            infoHash,
            peerId,
            IPAddress.Parse(ipAddress),
            port,
            0,
            0,
            left,
            "started",
            true,
            50);

    static InMemoryTrackerPeerStore CreateStore(IcecoldOptions? options = null)
    {
        var configured = Options.Create(options ?? new IcecoldOptions());
        return new InMemoryTrackerPeerStore(
            configured,
            new PeerWireAdvertisedPeerProvider(
                configured,
                new PeerWirePeerIdentity(),
                new PeerWireStandbyPolicy(configured)));
    }

    static IcecoldDbContext CreateTrackerDb()
        => new(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

    static TrackerController CreateTrackerController(IcecoldDbContext db, ITrackerPeerStore store, bool trackerEnabled)
    {
        var controller = new TrackerController(
            new TrackerAnnounceService(db, store),
            new TrackerScrapeService(db, store),
            Options.Create(new IcecoldOptions
            {
                Tracker = new TrackerOptions
                {
                    Enabled = trackerEnabled
                }
            }));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    sealed class RecordingPeerStore : ITrackerPeerStore
    {
        public bool WasCalled { get; private set; }

        public TrackerAnnounceResult Announce(TrackerAnnounceInput input)
        {
            WasCalled = true;
            return new TrackerAnnounceResult([], 0, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        }

        public TrackerScrapeStats Scrape(string infoHashHex)
        {
            WasCalled = true;
            return new TrackerScrapeStats(0, 0, 0);
        }

        public TrackerSwarmStats GetSwarmStats(string infoHashHex)
            => new(0, 0, 0);
    }
}
