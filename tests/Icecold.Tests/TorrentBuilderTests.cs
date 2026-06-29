using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
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

    static PublicUrlBuilder CreateUrls()
        => new(Options.Create(new IcecoldOptions { PublicBaseUrl = "https://example.test" }));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
