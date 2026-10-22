using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MonoTorrent;
using MonoTorrent.BEncoding;

namespace Icecold.Tests;

public sealed class MultiFileTorrentTests : IDisposable
{
    const int SmallPieceLength = 256 * 1024;

    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public MultiFileTorrentTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task BuildMultiFileAsync_Pads_Files_To_Piece_Boundaries()
    {
        var first = WriteRandomFile("a/first.bin", 300_000);
        var second = WriteRandomFile("b/second.bin", 10);
        var third = WriteRandomFile("third.bin", SmallPieceLength);
        var source = new LocalFileContentSource("local", root);
        var entries = new List<MultiFileTorrentEntry>
        {
            new(await source.GetMetadataAsync("a/first.bin", CancellationToken.None), "video/first.bin"),
            new(await source.GetMetadataAsync("b/second.bin", CancellationToken.None), "subs/second.bin"),
            new(await source.GetMetadataAsync("third.bin", CancellationToken.None), "third.bin")
        };
        var progress = new List<long>();

        var built = await new TorrentBuilder(CreateUrls())
            .BuildMultiFileAsync("Collection", entries, source, CancellationToken.None, progress.Add);

        var torrent = Torrent.Load(built.TorrentBytes);
        Assert.Equal("Collection", torrent.Name);
        Assert.Equal(SmallPieceLength, torrent.PieceLength);
        Assert.Equal(
            ["video/first.bin", "subs/second.bin", "third.bin"],
            torrent.Files.Select(f => f.Path.Replace('\\', '/')).ToArray());
        Assert.Equal(SmallPieceLength * 2 - 300_000, torrent.Files[0].Padding);
        Assert.Equal(SmallPieceLength - 10, torrent.Files[1].Padding);
        Assert.Equal(0, torrent.Files[2].Padding);
        Assert.Equal(first.Length + second.Length + third.Length, progress[^1]);

        // Pieces hash the files laid end to end with zero padding between them.
        var padded = new MemoryStream();
        padded.Write(first);
        padded.Write(new byte[SmallPieceLength * 2 - 300_000]);
        padded.Write(second);
        padded.Write(new byte[SmallPieceLength - 10]);
        padded.Write(third);
        var bytes = padded.ToArray();
        var info = (BEncodedDictionary)BEncodedDictionary.DecodeTorrent(built.TorrentBytes).torrent["info"];
        var pieces = ((BEncodedString)info["pieces"]).AsMemory().ToArray();
        Assert.Equal(bytes.Length / SmallPieceLength, built.PieceCount);
        for (var i = 0; i < built.PieceCount; i++)
            Assert.Equal(SHA1.HashData(bytes.AsSpan(i * SmallPieceLength, SmallPieceLength)), pieces.AsSpan(i * 20, 20).ToArray());

        var webSeed = ((BEncodedString)BEncodedDictionary.DecodeTorrent(built.TorrentBytes).torrent["url-list"]).Text;
        Assert.Equal($"https://example.test/webseed/{built.InfoHashHex}/", webSeed);
    }

    [Fact]
    public void Layout_Splits_Ranges_Into_Files_And_Padding()
    {
        var layout = TorrentContentLayout.MultiFile(
            [new("a", "a", 6, null), new("b", "b", 4, null), new("c", "c", 3, null)],
            pieceLength: 4);

        Assert.Equal(15, layout.TotalLength);
        Assert.Equal([0L, 8, 12], layout.Files.Select(f => f.Offset).ToArray());
        Assert.Null(layout.FindFile(6));
        Assert.Equal("b", layout.FindFile(8)!.SourcePath);

        var segments = layout.Segments(4, 10).ToList();
        Assert.Equal(
            [("a", 4L, 2L), (null!, 0, 2), ("b", 0, 4), ("c", 0, 2)],
            segments.Select(s => (s.File?.SourcePath ?? null!, s.FileOffset, s.Length)).ToArray());
    }

    [Fact]
    public async Task Index_Files_Builds_Torrent_And_Serves_Each_File_Over_WebSeed()
    {
        var first = WriteRandomFile("episodes/ep01.mkv", 300_000);
        var second = WriteRandomFile("episodes/ep01.ass", 1_234);
        await using var factory = CreateFactory(removeHostedServices: false);
        using var client = factory.CreateClient();

        using var response = await PostFilesAsync(client, new
        {
            source = "local",
            name = "My Show",
            files = new object[]
            {
                new { path = "episodes/ep01.mkv" },
                new { path = "episodes/ep01.ass", torrentPath = "subs/ep01 en.ass" }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await ReadJsonAsync(response)).GetProperty("id").GetString()!;

        var ready = await PollStatusAsync(client, id, "Ready");
        var infoHash = ready.GetProperty("infoHash").GetString()!;
        Assert.Equal(first.Length + second.Length, ready.GetProperty("contentLength").GetInt64());
        Assert.Empty(ready.GetProperty("locations").EnumerateArray());
        var files = ready.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["ep01.mkv", "subs/ep01 en.ass"], files.Select(f => f.GetProperty("torrentPath").GetString()!).ToArray());

        var torrentBytes = await client.GetByteArrayAsync($"/torrents/{infoHash}.torrent");
        var torrent = Torrent.Load(torrentBytes);
        Assert.Equal("My Show", torrent.Name);
        Assert.Equal(2, torrent.Files.Count);

        var magnet = await client.GetStringAsync($"/torrents/{infoHash}/magnet");
        Assert.Contains("ws=" + Uri.EscapeDataString($"https://example.test/webseed/{infoHash}/"), magnet);

        // Clients append the torrent name and the file's path to the multi-file webseed URL.
        Assert.Equal(first, await client.GetByteArrayAsync($"/webseed/{infoHash}/My%20Show/ep01.mkv"));
        Assert.Equal(second, await client.GetByteArrayAsync($"/webseed/{infoHash}/My%20Show/subs/ep01%20en.ass"));

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/webseed/{infoHash}/My%20Show/subs/ep01%20en.ass");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var rangeResponse = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(second.AsSpan(10, 10).ToArray(), await rangeResponse.Content.ReadAsByteArrayAsync());

        using var missing = await client.GetAsync($"/webseed/{infoHash}/My%20Show/nope.bin");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var wrongName = await client.GetAsync($"/webseed/{infoHash}/Other/ep01.mkv");
        Assert.Equal(HttpStatusCode.NotFound, wrongName.StatusCode);

        // Multi-file torrents are served from their indexed files only.
        using var addLocation = new HttpRequestMessage(HttpMethod.Post, $"/api/torrents/{id}/locations")
        {
            Content = JsonContent.Create(new { source = "local", path = "episodes/ep01.mkv" })
        };
        addLocation.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
        using var addLocationResponse = await client.SendAsync(addLocation);
        Assert.True(addLocationResponse.StatusCode == HttpStatusCode.Conflict, await addLocationResponse.Content.ReadAsStringAsync());

        // A changed file makes the torrent unavailable rather than serving bytes that no longer match.
        await File.WriteAllBytesAsync(Path.Combine(root, "episodes/ep01.ass"), new byte[1_235]);
        using var changed = await client.GetAsync($"/webseed/{infoHash}/My%20Show/ep01.mkv");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, changed.StatusCode);
    }

    [Fact]
    public async Task Index_Files_Resubmission_Reuses_Record_Only_For_Same_Manifest()
    {
        WriteRandomFile("one.bin", 100);
        WriteRandomFile("two.bin", 200);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new { source = "local", name = "Set", files = new object[] { new { path = "one.bin" }, new { path = "two.bin" } } };

        var first = await ReadIdAsync(await PostFilesAsync(client, request));
        var again = await ReadIdAsync(await PostFilesAsync(client, request));
        var reordered = await ReadIdAsync(await PostFilesAsync(client, new
        {
            source = "local",
            name = "Set",
            files = new object[] { new { path = "two.bin" }, new { path = "one.bin" } }
        }));

        Assert.Equal(first, again);
        Assert.NotEqual(first, reordered);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await db.Torrents.Include(t => t.Files).SingleAsync(t => t.Id == Guid.Parse(first));
        Assert.True(torrent.IsMultiFile);
        Assert.Equal(300, torrent.ContentLength);
        Assert.Equal(["one.bin", "two.bin"], torrent.Files.OrderBy(f => f.Index).Select(f => f.TorrentPath).ToArray());
    }

    [Theory]
    [InlineData("""{"source":"local","name":"Set","files":[]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"a/b","files":[{"path":"one.bin"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"..","files":[{"path":"one.bin"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin","torrentPath":"../x"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin","torrentPath":"a//b"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin","torrentPath":".pad/1"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin","torrentPath":"x"},{"path":"two.bin","torrentPath":"X"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin","torrentPath":"x"},{"path":"two.bin","torrentPath":"x/y"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin"},{"path":"empty.bin"}]}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"source":"local","name":"Set","files":[{"path":"one.bin"},{"path":"missing.bin"}]}""", HttpStatusCode.NotFound)]
    [InlineData("""{"source":"nope","name":"Set","files":[{"path":"one.bin"}]}""", HttpStatusCode.NotFound)]
    public async Task Index_Files_Rejects_Invalid_Requests(string body, HttpStatusCode expected)
    {
        WriteRandomFile("one.bin", 100);
        WriteRandomFile("two.bin", 200);
        await File.WriteAllBytesAsync(Path.Combine(root, "empty.bin"), []);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/files")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IcecoldDbContext>().Torrents.CountAsync());
    }

    byte[] WriteRandomFile(string relativePath, int length)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = RandomNumberGenerator.GetBytes(length);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    static PublicUrlBuilder CreateUrls()
        => new(Options.Create(new IcecoldOptions { PublicBaseUrl = "https://example.test" }));

    WebApplicationFactory<Program> CreateFactory(bool removeHostedServices = true)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var databaseName = Guid.NewGuid().ToString("n");

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Icecold:ContentSources:0:Name"] = "local",
                    ["Icecold:ContentSources:0:Type"] = "local",
                    ["Icecold:ContentSources:0:RootPath"] = root,
                    ["Icecold:AdminApiKey"] = "dev-admin-key",
                    ["Icecold:PublicBaseUrl"] = "https://example.test",
                    ["Icecold:PeerWire:Enabled"] = "false"
                });
            });

            builder.ConfigureServices(services =>
            {
                if (removeHostedServices)
                    services.RemoveAll<IHostedService>();

                services.RemoveAll<DbContextOptions<IcecoldDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IcecoldDbContext>>();
                services.AddDbContext<IcecoldDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        });

    static async Task<HttpResponseMessage> PostFilesAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/files")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
        return await client.SendAsync(request, CancellationToken.None);
    }

    static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using (response)
            return (await ReadJsonAsync(response)).GetProperty("id").GetString()!;
    }

    static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
        return document.RootElement.Clone();
    }

    static async Task<JsonElement> PollStatusAsync(HttpClient client, string id, string expectedStatus)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/torrents/{id}");
            request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
            using var response = await client.SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var root = await ReadJsonAsync(response);
            if (root.GetProperty("status").GetString() == expectedStatus)
                return root;

            await Task.Delay(100, CancellationToken.None);
        }

        throw new TimeoutException($"Torrent '{id}' did not reach status '{expectedStatus}'.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
