using System.Collections.Concurrent;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Indexing;
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

// The claim (UPDATE ... FOR UPDATE SKIP LOCKED RETURNING), the fenced ExecuteUpdate writes and the
// locked finalize only run against a relational provider, so they are exercised on PostgreSQL.
public sealed class PostgresIndexingQueueTests : IAsyncLifetime
{
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));
    ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        await postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<IcecoldDbContext>(options => options.UseNpgsql(postgres.GetConnectionString()));
        services.AddScoped<IndexingQueue>();
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
    public async Task Concurrent_Claimers_Never_Get_The_Same_Job()
    {
        var seeded = new List<Guid>();
        for (var i = 0; i < 40; i++)
            seeded.Add(await SeedAsync(TorrentStatus.Pending));

        var claimed = new ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            while (true)
            {
                await using var scope = provider.CreateAsyncScope();
                var torrent = await scope.ServiceProvider.GetRequiredService<IndexingQueue>()
                    .ClaimNextAsync($"worker-{worker}", Lease, CancellationToken.None);
                if (torrent is null)
                    return;
                claimed.Add(torrent.Id);
            }
        })));

        Assert.Equal(seeded.Count, claimed.Count);
        Assert.Equal(seeded.OrderBy(id => id), claimed.OrderBy(id => id));
    }

    [Fact]
    public async Task Claim_Order_Reclaim_Fencing_And_Position()
    {
        var now = DateTimeOffset.UtcNow;
        var low = await SeedAsync(TorrentStatus.Pending, availableAt: now.AddMinutes(-10));
        var high = await SeedAsync(TorrentStatus.Pending, priority: 5, availableAt: now.AddMinutes(-1));
        _ = await SeedAsync(TorrentStatus.Pending, availableAt: now.AddMinutes(10));
        var abandoned = await SeedAsync(TorrentStatus.Hashing, configure: t =>
        {
            t.LeaseOwner = "dead";
            t.LeaseExpiresAt = now.AddMinutes(-1);
        });

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IndexingQueue>();

        Assert.Equal(1, await queue.GetQueuePositionAsync(await db.Torrents.AsNoTracking().SingleAsync(t => t.Id == low), CancellationToken.None));

        var first = await queue.ClaimNextAsync("a", Lease, CancellationToken.None);
        Assert.Equal(abandoned, first!.Id);
        Assert.Equal("a", first.LeaseOwner);
        Assert.Equal(1, first.Attempts);

        Assert.Equal(high, (await queue.ClaimNextAsync("b", Lease, CancellationToken.None))!.Id);
        Assert.Equal(low, (await queue.ClaimNextAsync("c", Lease, CancellationToken.None))!.Id);
        Assert.Null(await queue.ClaimNextAsync("d", Lease, CancellationToken.None));

        Assert.False(await queue.RenewLeaseAsync(abandoned, "dead", Lease, 1, CancellationToken.None));
        Assert.True(await queue.RenewLeaseAsync(abandoned, "a", Lease, 42, CancellationToken.None));
        Assert.True(await queue.RequeueAsync(high, "b", now.AddMinutes(5), "retry later", CancellationToken.None));
        Assert.True(await queue.ReleaseAsync(low, "c", CancellationToken.None));

        var stored = await db.Torrents.AsNoTracking().ToDictionaryAsync(t => t.Id);
        Assert.Equal(42, stored[abandoned].HashedBytes);
        Assert.Equal(TorrentStatus.Pending, stored[high].Status);
        Assert.Null(stored[high].LeaseOwner);
        Assert.Equal(TorrentStatus.Pending, stored[low].Status);
        Assert.Equal(0, stored[low].Attempts);
    }

    [Fact]
    public async Task Worker_Indexes_End_To_End_On_PostgreSQL()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "postgres payload");
        var options = Options.Create(new IcecoldOptions
        {
            PublicBaseUrl = "http://example.test",
            ContentSources = [new ContentSourceOptions { Name = "local", Type = "local", RootPath = root }],
            Indexing = new IndexingOptions { MaxConcurrency = 2 }
        });
        var registry = new ContentSourceRegistry(options, new TestWebHostEnvironment(root));
        var metadata = await registry.GetRequired("local").GetMetadataAsync("payload.bin", CancellationToken.None);
        var id = await SeedAsync(TorrentStatus.Pending, configure: t =>
        {
            t.SourcePath = metadata.Path;
            t.ContentLength = metadata.Length;
            t.ContentVersion = metadata.Version;
        });

        using var worker = new IndexingWorker(
            new IndexingSignal(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            new TorrentBuilder(new PublicUrlBuilder(options)),
            options,
            NullLogger<IndexingWorker>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(20)
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                await using var scope = provider.CreateAsyncScope();
                var torrent = await scope.ServiceProvider.GetRequiredService<IcecoldDbContext>()
                    .Torrents.AsNoTracking().Include(t => t.Locations).SingleAsync(t => t.Id == id);
                if (torrent.Status == TorrentStatus.Ready)
                {
                    Assert.NotNull(torrent.InfoHashHex);
                    Assert.Null(torrent.LeaseOwner);
                    Assert.Equal(metadata.Length, torrent.HashedBytes);
                    Assert.Single(torrent.Locations);
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail("Torrent was not indexed before the timeout.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    async Task<Guid> SeedAsync(
        TorrentStatus status,
        int priority = 0,
        DateTimeOffset? availableAt = null,
        Action<TorrentRecord>? configure = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var now = DateTimeOffset.UtcNow;
        var torrent = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = $"{Guid.NewGuid():n}.bin",
            DisplayName = "payload.bin",
            ContentLength = 1,
            ContentVersion = Guid.NewGuid().ToString("n"),
            Status = status,
            Priority = priority,
            AvailableAt = availableAt ?? now.AddSeconds(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        configure?.Invoke(torrent);
        db.Torrents.Add(torrent);
        await db.SaveChangesAsync();
        return torrent.Id;
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
