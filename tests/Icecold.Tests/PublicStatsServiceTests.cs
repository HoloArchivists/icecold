using System.Net;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Stats;
using Icecold.Api.Tracker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class PublicStatsServiceTests : IDisposable
{
    readonly IcecoldDbContext db = new(new DbContextOptionsBuilder<IcecoldDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
        .Options);
    readonly MemoryCache cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task GetAsync_Returns_Public_Torrent_Tracker_And_Serving_Stats()
    {
        AddTorrent(TorrentStatus.Ready, 100, TorrentLocationStatus.Active);
        AddTorrent(TorrentStatus.Ready, 200, TorrentLocationStatus.Missing);
        AddTorrent(TorrentStatus.Pending, 300);
        AddTorrent(TorrentStatus.Duplicate, 100);
        AddTorrent(TorrentStatus.Failed, 50);
        await db.SaveChangesAsync();

        var options = CreateOptions(cacheSeconds: 0);
        var peerStore = CreatePeerStore(options);
        var infoHash = new string('a', 40);
        _ = peerStore.Announce(CreateAnnounce(infoHash, peerIdByte: 1, left: 0));
        _ = peerStore.Announce(CreateAnnounce(infoHash, peerIdByte: 2, left: 10));
        _ = peerStore.Announce(CreateAnnounce(infoHash, peerIdByte: 2, left: 0, eventName: "completed"));
        var service = new PublicStatsService(db, peerStore, options, cache);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(2, result.Torrents.Ready);
        Assert.Equal(1, result.Torrents.Seeding);
        Assert.Equal(300, result.Torrents.ReadyBytes);
        Assert.Equal(100, result.Torrents.SeedingBytes);
        Assert.Equal(200, result.Torrents.LargestReadyBytes);

        Assert.Equal(1, result.Tracker.ActiveInfoHashes);
        Assert.Equal(2, result.Tracker.Peers);
        Assert.Equal(2, result.Tracker.Seeders);
        Assert.Equal(0, result.Tracker.Leechers);
        Assert.Equal(1, result.Tracker.CompletedAnnounces);

        Assert.True(result.Serving.WebSeedEnabled);
        Assert.True(result.Serving.PeerWireEnabled);
    }

    [Fact]
    public async Task GetAsync_Uses_Configured_Cache()
    {
        AddTorrent(TorrentStatus.Ready, 100, TorrentLocationStatus.Active);
        await db.SaveChangesAsync();
        var options = CreateOptions(cacheSeconds: 60);
        var service = new PublicStatsService(db, CreatePeerStore(options), options, cache);

        var first = await service.GetAsync(CancellationToken.None);
        AddTorrent(TorrentStatus.Ready, 100, TorrentLocationStatus.Active);
        await db.SaveChangesAsync();
        var second = await service.GetAsync(CancellationToken.None);

        Assert.Equal(1, first.Torrents.Ready);
        Assert.Equal(1, second.Torrents.Ready);
        Assert.Equal(first.GeneratedAt, second.GeneratedAt);
    }

    [Fact]
    public void StatsOptionsValidator_Rejects_Invalid_Cache_Duration()
    {
        var result = new StatsOptionsValidator().Validate(null, new IcecoldOptions
        {
            Stats = new StatsOptions { CacheSeconds = 301 }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:Stats:CacheSeconds must be between 0 and 300.", result.Failures);
    }

    void AddTorrent(TorrentStatus status, long contentLength, TorrentLocationStatus? locationStatus = null)
    {
        var now = DateTimeOffset.UtcNow;
        var torrent = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = $"payload-{Guid.NewGuid():n}.bin",
            DisplayName = "payload.bin",
            ContentLength = contentLength,
            ContentVersion = Guid.NewGuid().ToString("n"),
            Status = status,
            InfoHashHex = status == TorrentStatus.Ready ? new string('a', 40) : null,
            PieceLength = status == TorrentStatus.Ready ? 256 * 1024 : null,
            PieceCount = status == TorrentStatus.Ready ? 1 : null,
            TorrentBytes = status == TorrentStatus.Ready ? [1, 2, 3] : null,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = status == TorrentStatus.Ready ? now : null
        };
        db.Torrents.Add(torrent);

        if (locationStatus is null)
            return;

        db.TorrentLocations.Add(new TorrentLocationRecord
        {
            Id = Guid.NewGuid(),
            TorrentId = torrent.Id,
            SourceName = "local",
            SourcePath = torrent.SourcePath,
            ContentLength = contentLength,
            ContentVersion = torrent.ContentVersion,
            Status = locationStatus.Value,
            IsPrimary = locationStatus == TorrentLocationStatus.Active,
            Priority = 0,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    static IOptions<IcecoldOptions> CreateOptions(int cacheSeconds)
        => Options.Create(new IcecoldOptions
        {
            Stats = new StatsOptions { CacheSeconds = cacheSeconds },
            WebSeed = new WebSeedOptions { Enabled = true },
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                AdvertisedIp = "127.0.0.10",
                AdvertisedPort = 6881
            }
        });

    static InMemoryTrackerPeerStore CreatePeerStore(IOptions<IcecoldOptions> options)
        => new(options, new PeerWireAdvertisedPeerProvider(options, new PeerWirePeerIdentity()));

    static TrackerAnnounceInput CreateAnnounce(
        string infoHash,
        byte peerIdByte,
        long left,
        string? eventName = null)
        => new(
            infoHash,
            Enumerable.Repeat(peerIdByte, 20).ToArray(),
            IPAddress.Parse($"127.0.0.{peerIdByte}"),
            6880 + peerIdByte,
            0,
            0,
            left,
            eventName,
            true,
            50);

    public void Dispose()
    {
        db.Dispose();
        cache.Dispose();
    }
}
