using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MonoTorrent.BEncoding;

namespace Icecold.Tests;

public sealed class TorrentBuilderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public TorrentBuilderTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task BuildSingleFileAsync_Is_Deterministic_For_Same_Content()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var builder = new TorrentBuilder(CreateUrls());

        var first = await builder.BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        var second = await builder.BuildSingleFileAsync(metadata, source, null, CancellationToken.None);

        Assert.Equal(first.InfoHashHex, second.InfoHashHex);
        Assert.Equal(first.TorrentBytes, second.TorrentBytes);
        Assert.Equal(40, first.InfoHashHex.Length);
        _ = BEncodedDictionary.DecodeTorrent(first.TorrentBytes);
    }

    [Fact]
    public async Task BuildMagnet_Includes_InfoHash_Tracker_And_WebSeed()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var urls = CreateUrls();
        var torrent = await new TorrentBuilder(urls).BuildSingleFileAsync(metadata, source, null, CancellationToken.None);

        var magnet = urls.BuildMagnet(new TorrentRecord
        {
            InfoHashHex = torrent.InfoHashHex,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length
        });

        Assert.Contains($"xt=urn:btih:{torrent.InfoHashHex}", magnet);
        Assert.Contains("dn=payload.bin", magnet);
        Assert.Contains("tr=https%3A%2F%2Fexample.test%2Fannounce", magnet);
        Assert.Contains("ws=https%3A%2F%2Fexample.test%2Fwebseed%2F", magnet);
    }

    [Fact]
    public async Task BuildMagnet_Uses_WebSeed_Public_Base_Url_Override()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var urls = CreateUrls(webSeedPublicBaseUrl: "https://cdn.example.test/cache/");
        var torrent = await new TorrentBuilder(urls).BuildSingleFileAsync(metadata, source, null, CancellationToken.None);

        var magnet = urls.BuildMagnet(new TorrentRecord
        {
            InfoHashHex = torrent.InfoHashHex,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length
        });

        Assert.Contains("tr=https%3A%2F%2Fexample.test%2Fannounce", magnet);
        Assert.Contains("ws=https%3A%2F%2Fcdn.example.test%2Fcache%2Fwebseed%2F", magnet);
    }

    [Fact]
    public async Task BuildMagnet_And_Torrent_File_Exclude_WebSeed_When_Disabled()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var urls = CreateUrls(webSeedEnabled: false);
        var torrent = await new TorrentBuilder(urls).BuildSingleFileAsync(metadata, source, null, CancellationToken.None);

        var magnet = urls.BuildMagnet(new TorrentRecord
        {
            InfoHashHex = torrent.InfoHashHex,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length
        });
        var decoded = BEncodedDictionary.DecodeTorrent(torrent.TorrentBytes).torrent;

        Assert.Null(torrent.WebSeedUrl);
        Assert.DoesNotContain("&ws=", magnet);
        Assert.False(decoded.ContainsKey(new BEncodedString("url-list")));
    }

    [Fact]
    public async Task BuildSingleFileAsync_Uses_Custom_Display_Name()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);

        var result = await new TorrentBuilder(CreateUrls())
            .BuildSingleFileAsync(metadata, source, "custom.bin", CancellationToken.None);
        var decoded = BEncodedDictionary.DecodeTorrent(result.TorrentBytes).torrent;
        var info = (BEncodedDictionary)decoded["info"];

        Assert.Equal("custom.bin", info["name"].ToString());
    }

    [Fact]
    public async Task GetTorrentFileAsync_Strips_Stored_WebSeed_When_Disabled()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var built = await new TorrentBuilder(CreateUrls()).BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        await using var db = CreateDb(built, metadata);
        var service = new TorrentMetadataService(db, CreateUrls(webSeedEnabled: false));

        var result = await service.GetTorrentFileAsync(built.InfoHashHex, CancellationToken.None);
        var decoded = BEncodedDictionary.DecodeTorrent(result!.Bytes).torrent;

        Assert.False(decoded.ContainsKey(new BEncodedString("url-list")));
    }

    [Fact]
    public async Task GetTorrentFileAsync_Adds_WebSeed_When_Enabled_For_Stored_Torrent_Without_WebSeed()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "payload bytes");
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var built = await new TorrentBuilder(CreateUrls(webSeedEnabled: false)).BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        await using var db = CreateDb(built, metadata);
        var service = new TorrentMetadataService(db, CreateUrls());

        var result = await service.GetTorrentFileAsync(built.InfoHashHex, CancellationToken.None);
        var decoded = BEncodedDictionary.DecodeTorrent(result!.Bytes).torrent;
        var webSeed = decoded[new BEncodedString("url-list")].ToString();

        Assert.Equal($"https://example.test/webseed/{built.InfoHashHex}/payload.bin", webSeed);
    }

    static IcecoldDbContext CreateDb(TorrentBuildResult built, ContentMetadata metadata)
    {
        var db = new IcecoldDbContext(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

        db.Torrents.Add(new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = metadata.SourceName,
            SourcePath = metadata.Path,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            ContentLastModified = metadata.LastModified,
            Status = TorrentStatus.Ready,
            InfoHashHex = built.InfoHashHex,
            TorrentBytes = built.TorrentBytes,
            PieceLength = built.PieceLength,
            PieceCount = built.PieceCount,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
        return db;
    }

    static PublicUrlBuilder CreateUrls(bool webSeedEnabled = true, string? webSeedPublicBaseUrl = null)
        => new(Options.Create(new IcecoldOptions
        {
            PublicBaseUrl = "https://example.test",
            WebSeed = new WebSeedOptions
            {
                Enabled = webSeedEnabled,
                PublicBaseUrl = webSeedPublicBaseUrl
            }
        }));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
