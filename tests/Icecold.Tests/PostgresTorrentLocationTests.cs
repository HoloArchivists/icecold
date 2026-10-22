using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Icecold.Tests;

// EF InMemory ignores unique indexes, so primary-location swaps are exercised against a real
// PostgreSQL to cover the filtered unique index IX_torrent_locations_Primary.
public sealed class PostgresTorrentLocationTests : IAsyncLifetime
{
    readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));
    ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "hot"));
        Directory.CreateDirectory(Path.Combine(root, "cold"));
        await postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<IcecoldDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()));
        provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IcecoldDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await postgres.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Swapping_Primary_Location_Does_Not_Violate_Primary_Unique_Index()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "hot", "payload.bin"), "same bytes");
        await File.WriteAllTextAsync(Path.Combine(root, "cold", "payload.bin"), "same bytes");
        var options = CreateOptions();
        var registry = new ContentSourceRegistry(options, new TestWebHostEnvironment(root));
        var builder = new TorrentBuilder(new PublicUrlBuilder(options));
        var hot = registry.GetRequired("hot");
        var hotMetadata = await hot.GetMetadataAsync("payload.bin", CancellationToken.None);
        var built = await builder.BuildSingleFileAsync(hotMetadata, hot, null, CancellationToken.None);
        var torrentId = await SeedReadyTorrentAsync(built, hotMetadata);
        var service = new TorrentLocationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            builder,
            NullLogger<TorrentLocationService>.Instance);

        // Insert + promote in one save.
        var added = await service.AddLocationAsync(
            torrentId,
            new AddTorrentLocationRequest("cold", "payload.bin", MakePrimary: true),
            CancellationToken.None);
        Assert.Equal(TorrentLocationOperationStatus.Success, added.Status);
        var coldId = added.Location!.Id;
        Assert.Equal(coldId, await SinglePrimaryIdAsync(torrentId));
        var hotId = await LocationIdAsync(torrentId, "hot");

        // Swap back and forth so both EF update orderings are exercised.
        for (var i = 0; i < 6; i++)
        {
            var target = i % 2 == 0 ? hotId : coldId;
            var result = await service.SetPrimaryAsync(torrentId, target, CancellationToken.None);
            Assert.Equal(TorrentLocationOperationStatus.Success, result.Status);
            Assert.Equal(target, await SinglePrimaryIdAsync(torrentId));
        }

        // Disabling the primary promotes the remaining active location.
        var disabled = await service.DisableAsync(torrentId, coldId, CancellationToken.None);
        Assert.Equal(TorrentLocationOperationStatus.Success, disabled.Status);
        Assert.Equal(hotId, await SinglePrimaryIdAsync(torrentId));
    }

    async Task<Guid> SinglePrimaryIdAsync(Guid torrentId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var primaries = await db.TorrentLocations.AsNoTracking()
            .Where(l => l.TorrentId == torrentId && l.IsPrimary)
            .Select(l => l.Id)
            .ToListAsync();

        return Assert.Single(primaries);
    }

    async Task<Guid> LocationIdAsync(Guid torrentId, string sourceName)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        return await db.TorrentLocations.AsNoTracking()
            .Where(l => l.TorrentId == torrentId && l.SourceName == sourceName)
            .Select(l => l.Id)
            .SingleAsync();
    }

    async Task<Guid> SeedReadyTorrentAsync(TorrentBuildResult built, ContentMetadata metadata)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
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
            InfoHashHex = built.InfoHashHex,
            PieceLength = built.PieceLength,
            PieceCount = built.PieceCount,
            TorrentBytes = built.TorrentBytes,
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
        await db.SaveChangesAsync();
        return torrent.Id;
    }

    IOptions<IcecoldOptions> CreateOptions()
        => Options.Create(new IcecoldOptions
        {
            PublicBaseUrl = "http://example.test",
            ContentSources =
            [
                new ContentSourceOptions { Name = "hot", Type = "local", RootPath = Path.Combine(root, "hot") },
                new ContentSourceOptions { Name = "cold", Type = "local", RootPath = Path.Combine(root, "cold") }
            ]
        });

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
