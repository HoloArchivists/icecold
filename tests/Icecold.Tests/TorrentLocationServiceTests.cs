using System.Text;
using Icecold.Api.Controllers;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Icecold.Api.WebSeed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Icecold.Tests;

public sealed class TorrentLocationServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));
    readonly string hotRoot;
    readonly string coldRoot;

    public TorrentLocationServiceTests()
    {
        hotRoot = Path.Combine(root, "hot");
        coldRoot = Path.Combine(root, "cold");
        Directory.CreateDirectory(hotRoot);
        Directory.CreateDirectory(coldRoot);
    }

    [Fact]
    public async Task WebSeed_Falls_Back_From_Missing_Primary_To_Alternate_Location()
    {
        var content = Encoding.ASCII.GetBytes("tiered storage payload");
        await File.WriteAllBytesAsync(Path.Combine(hotRoot, "payload.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(coldRoot, "payload.bin"), content);

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);

        var coldSource = new LocalFileContentSource("cold", coldRoot);
        var coldMetadata = await coldSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        await AddLocationAsync(db, torrentResult, coldMetadata, isPrimary: false, priority: 10);

        File.Delete(Path.Combine(hotRoot, "payload.bin"));
        await using var provider = CreateProvider(db);
        var webSeed = new WebSeedService(CreateLocationService(provider));

        var result = await webSeed.OpenAsync(torrentResult.InfoHashHex, null, CancellationToken.None);

        Assert.Equal(WebSeedOpenStatus.Opened, result.Status);
        await using var stream = result.Stream!;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Assert.Equal(content, memory.ToArray());

        var hotLocation = await db.TorrentLocations.SingleAsync(l => l.SourceName == "hot");
        var coldLocation = await db.TorrentLocations.SingleAsync(l => l.SourceName == "cold");
        Assert.Equal(TorrentLocationStatus.Missing, hotLocation.Status);
        Assert.Equal(TorrentLocationStatus.Active, coldLocation.Status);
    }

    [Fact]
    public async Task AddLocation_Can_Verify_Alternate_And_Make_It_Primary()
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), "same bytes");
        await File.WriteAllTextAsync(Path.Combine(coldRoot, "payload.bin"), "same bytes");

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);
        await using var provider = CreateProvider(db);
        var locations = CreateLocationService(provider);
        var torrentId = await db.Torrents.Select(t => t.Id).SingleAsync();

        var result = await locations.AddLocationAsync(
            torrentId,
            new AddTorrentLocationRequest("cold", "payload.bin", MakePrimary: true),
            CancellationToken.None);

        Assert.Equal(TorrentLocationOperationStatus.Success, result.Status);
        Assert.Equal("cold", result.Location!.Source);

        var stored = await db.TorrentLocations.OrderBy(l => l.SourceName).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.False(stored.Single(l => l.SourceName == "hot").IsPrimary);
        Assert.True(stored.Single(l => l.SourceName == "cold").IsPrimary);
    }

    [Fact]
    public async Task WebSeed_Does_Not_Serve_Disabled_Only_Location_Through_Legacy_Fallback()
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), "still present");

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);
        var location = await db.TorrentLocations.SingleAsync();
        location.Status = TorrentLocationStatus.Disabled;
        location.IsPrimary = false;
        await db.SaveChangesAsync();
        await using var provider = CreateProvider(db);
        var webSeed = new WebSeedService(CreateLocationService(provider));

        var result = await webSeed.OpenAsync(torrentResult.InfoHashHex, null, CancellationToken.None);

        Assert.Equal(WebSeedOpenStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task WebSeed_Returns_Unavailable_When_Known_Torrent_Has_No_Readable_Location()
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), "missing soon");

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);
        File.Delete(Path.Combine(hotRoot, "payload.bin"));
        await using var provider = CreateProvider(db);
        var webSeed = new WebSeedService(CreateLocationService(provider));

        var result = await webSeed.OpenAsync(torrentResult.InfoHashHex, null, CancellationToken.None);

        Assert.Equal(WebSeedOpenStatus.Unavailable, result.Status);
        Assert.Equal(TorrentLocationStatus.Missing, (await db.TorrentLocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task WebSeed_Returns_Range_Not_Satisfiable_For_Unsatisfiable_Range()
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), "hello");

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);
        await using var provider = CreateProvider(db);
        var webSeed = new WebSeedService(CreateLocationService(provider));

        var result = await webSeed.OpenAsync(torrentResult.InfoHashHex, "bytes=99-100", CancellationToken.None);

        Assert.Equal(WebSeedOpenStatus.RangeNotSatisfiable, result.Status);
        Assert.Equal(5, result.ContentLength);
    }

    [Fact]
    public async Task WebSeed_Controller_Returns_416_With_Content_Range_For_Unsatisfiable_Range()
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), "hello");

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, hotMetadata);
        await using var provider = CreateProvider(db);
        var webSeed = new WebSeedService(CreateLocationService(provider));
        var context = new DefaultHttpContext();
        context.Request.Headers[HeaderNames.Range] = "bytes=99-100";
        var controller = new WebSeedController(
            webSeed,
            Options.Create(new IcecoldOptions
            {
                WebSeed = new WebSeedOptions { Enabled = true }
            }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context
            }
        };

        var result = await controller.Get(torrentResult.InfoHashHex, "payload.bin", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers[HeaderNames.AcceptRanges].ToString());
        Assert.Equal("bytes */5", context.Response.Headers[HeaderNames.ContentRange].ToString());
        Assert.Equal(0, context.Response.ContentLength);
    }

    [Theory]
    [InlineData("bytes=abc")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-0,2-3")]
    [InlineData("bytes=3-1")]
    public async Task WebSeed_Controller_Ignores_Invalid_Or_Multi_Range_And_Serves_Full_Body(string rangeHeader)
    {
        var (controller, context, infoHash) = await CreateWebSeedControllerAsync("hello");
        context.Request.Headers[HeaderNames.Range] = rangeHeader;

        var result = await controller.Get(infoHash, "payload.bin", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(5, context.Response.ContentLength);
        Assert.Equal("hello", Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task WebSeed_Controller_Sends_Caching_Headers_On_Full_And_Partial_Responses()
    {
        var (controller, context, infoHash) = await CreateWebSeedControllerAsync("hello");

        _ = await controller.Get(infoHash, "payload.bin", CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers[HeaderNames.AcceptRanges].ToString());
        Assert.Equal("public, max-age=86400", context.Response.Headers[HeaderNames.CacheControl].ToString());
        Assert.Equal($"\"{infoHash}\"", context.Response.Headers[HeaderNames.ETag].ToString());

        var partialContext = CreateWebSeedHttpContext();
        partialContext.Request.Headers[HeaderNames.Range] = "bytes=1-2";
        controller.ControllerContext = new ControllerContext { HttpContext = partialContext };

        _ = await controller.Get(infoHash, "payload.bin", CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, partialContext.Response.StatusCode);
        Assert.Equal("bytes 1-2/5", partialContext.Response.Headers[HeaderNames.ContentRange].ToString());
        Assert.Equal("public, max-age=86400", partialContext.Response.Headers[HeaderNames.CacheControl].ToString());
        Assert.Equal($"\"{infoHash}\"", partialContext.Response.Headers[HeaderNames.ETag].ToString());
        Assert.Equal("el", Encoding.ASCII.GetString(((MemoryStream)partialContext.Response.Body).ToArray()));
    }

    [Fact]
    public async Task WebSeed_Controller_Answers_Head_Without_Body()
    {
        var (controller, context, infoHash) = await CreateWebSeedControllerAsync("hello");
        context.Request.Method = HttpMethods.Head;

        var result = await controller.Get(infoHash, "payload.bin", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(5, context.Response.ContentLength);
        Assert.Equal($"\"{infoHash}\"", context.Response.Headers[HeaderNames.ETag].ToString());
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task WebSeed_Controller_Returns_503_With_Retry_After_When_No_Location_Is_Readable()
    {
        var (controller, context, infoHash) = await CreateWebSeedControllerAsync("missing soon");
        File.Delete(Path.Combine(hotRoot, "payload.bin"));

        var result = await controller.Get(infoHash, "payload.bin", CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("60", context.Response.Headers[HeaderNames.RetryAfter].ToString());
        Assert.True(string.IsNullOrEmpty(context.Response.Headers[HeaderNames.ETag].ToString()));
    }

    async Task<(WebSeedController Controller, DefaultHttpContext Context, string InfoHash)> CreateWebSeedControllerAsync(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(hotRoot, "payload.bin"), content);

        var hotSource = new LocalFileContentSource("hot", hotRoot);
        var hotMetadata = await hotSource.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await CreateBuilder().BuildSingleFileAsync(hotMetadata, hotSource, null, CancellationToken.None);
        // In-memory db and provider are left to the GC so the controller can outlive this helper.
        var db = CreateDb(torrentResult, hotMetadata);
        var provider = CreateProvider(db);
        var context = CreateWebSeedHttpContext();
        var controller = new WebSeedController(
            new WebSeedService(CreateLocationService(provider)),
            Options.Create(new IcecoldOptions
            {
                WebSeed = new WebSeedOptions { Enabled = true }
            }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context
            }
        };

        return (controller, context, torrentResult.InfoHashHex);
    }

    static DefaultHttpContext CreateWebSeedHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        return context;
    }

    IcecoldDbContext CreateDb(TorrentBuildResult torrentResult, ContentMetadata metadata)
    {
        var db = new IcecoldDbContext(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

        AddTorrent(db, torrentResult, metadata);
        db.SaveChanges();
        return db;
    }

    static void AddTorrent(IcecoldDbContext db, TorrentBuildResult torrentResult, ContentMetadata metadata)
    {
        var now = DateTimeOffset.UtcNow;
        var torrent = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = metadata.SourceName,
            SourcePath = metadata.Path,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            ContentLastModified = metadata.LastModified,
            Status = TorrentStatus.Ready,
            InfoHashHex = torrentResult.InfoHashHex,
            PieceLength = torrentResult.PieceLength,
            PieceCount = torrentResult.PieceCount,
            TorrentBytes = torrentResult.TorrentBytes,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };

        db.Torrents.Add(torrent);
        db.TorrentLocations.Add(new TorrentLocationRecord
        {
            Id = Guid.NewGuid(),
            TorrentId = torrent.Id,
            SourceName = metadata.SourceName,
            SourcePath = metadata.Path,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            ContentLastModified = metadata.LastModified,
            Status = TorrentLocationStatus.Active,
            IsPrimary = true,
            Priority = 0,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    static async Task AddLocationAsync(
        IcecoldDbContext db,
        TorrentBuildResult torrentResult,
        ContentMetadata metadata,
        bool isPrimary,
        int priority)
    {
        var now = DateTimeOffset.UtcNow;
        var torrentId = await db.Torrents
            .Where(t => t.InfoHashHex == torrentResult.InfoHashHex)
            .Select(t => t.Id)
            .SingleAsync();

        db.TorrentLocations.Add(new TorrentLocationRecord
        {
            Id = Guid.NewGuid(),
            TorrentId = torrentId,
            SourceName = metadata.SourceName,
            SourcePath = metadata.Path,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            ContentLastModified = metadata.LastModified,
            Status = TorrentLocationStatus.Active,
            IsPrimary = isPrimary,
            Priority = priority,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    TorrentLocationService CreateLocationService(ServiceProvider provider)
        => new(
            new TestScopeFactory(provider),
            CreateRegistry(),
            CreateBuilder(),
            NullLogger<TorrentLocationService>.Instance);

    TorrentBuilder CreateBuilder()
        => new(new PublicUrlBuilder(Options.Create(new IcecoldOptions { PublicBaseUrl = "http://example.test" })));

    ContentSourceRegistry CreateRegistry()
        => new(
            Options.Create(new IcecoldOptions
            {
                ContentSources =
                [
                    new ContentSourceOptions
                    {
                        Name = "hot",
                        Type = "local",
                        RootPath = hotRoot
                    },
                    new ContentSourceOptions
                    {
                        Name = "cold",
                        Type = "local",
                        RootPath = coldRoot
                    }
                ]
            }),
            new TestWebHostEnvironment(root));

    static ServiceProvider CreateProvider(IcecoldDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    sealed class TestScopeFactory(ServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => provider.CreateScope();
    }

    sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Icecold.Tests";

        public string WebRootPath { get; set; } = contentRootPath;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
