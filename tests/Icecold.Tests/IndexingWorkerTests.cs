using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class IndexingWorkerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public IndexingWorkerTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Worker_Drains_More_Pending_Rows_Than_Queue_Capacity()
    {
        var interceptor = new FailReadyTransitionInterceptor(failures: 0);
        await using var provider = CreateProvider(interceptor);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await SeedPendingAsync(provider, $"file-{i}.bin", $"payload {i}"));

        using var worker = CreateWorker(provider, queueCapacity: 1);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(provider, db => db.Torrents.AllAsync(t => t.Status == TorrentStatus.Ready));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Transient_Database_Error_Requeues_Instead_Of_Failing()
    {
        // Fails every finalize retry of the first attempt, then lets the second attempt through.
        var interceptor = new FailReadyTransitionInterceptor(failures: 16);
        await using var provider = CreateProvider(interceptor);
        var id = await SeedPendingAsync(provider, "payload.bin", "payload");

        using var worker = CreateWorker(provider, queueCapacity: 10);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(provider, db => db.Torrents.AnyAsync(t => t.Id == id && t.Status == TorrentStatus.Ready));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var torrent = await scope.ServiceProvider.GetRequiredService<IcecoldDbContext>().Torrents.SingleAsync();
        Assert.Equal(2, torrent.Attempts);
    }

    [Fact]
    public async Task Retryable_Failure_Past_Max_Attempts_Marks_Failed()
    {
        var interceptor = new FailReadyTransitionInterceptor(failures: int.MaxValue);
        await using var provider = CreateProvider(interceptor);
        var id = await SeedPendingAsync(provider, "payload.bin", "payload", attempts: IndexingWorker.MaxIndexingAttempts - 1);

        using var worker = CreateWorker(provider, queueCapacity: 10);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(provider, db => db.Torrents.AnyAsync(t => t.Id == id && t.Status == TorrentStatus.Failed));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var scope = provider.CreateAsyncScope();
        var torrent = await scope.ServiceProvider.GetRequiredService<IcecoldDbContext>().Torrents.SingleAsync();
        Assert.Equal(IndexingWorker.MaxIndexingAttempts, torrent.Attempts);
        Assert.Contains("Gave up after", torrent.Error);
    }

    ServiceProvider CreateProvider(FailReadyTransitionInterceptor interceptor)
    {
        var databaseName = Guid.NewGuid().ToString("n");
        var services = new ServiceCollection();
        services.AddDbContext<IcecoldDbContext>(options => options
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(interceptor));
        services.AddScoped<IndexingClaimService>();
        return services.BuildServiceProvider();
    }

    IndexingWorker CreateWorker(ServiceProvider provider, int queueCapacity)
    {
        var options = Options.Create(new IcecoldOptions
        {
            PublicBaseUrl = "http://example.test",
            ContentSources = [new ContentSourceOptions { Name = "local", Type = "local", RootPath = root }],
            Indexing = new IndexingOptions { MaxConcurrency = 1, QueueCapacity = queueCapacity }
        });

        return new IndexingWorker(
            new ChannelIndexingQueue(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ContentSourceRegistry(options, new TestWebHostEnvironment(root)),
            new TorrentBuilder(new PublicUrlBuilder(options)),
            options,
            NullLogger<IndexingWorker>.Instance)
        {
            PendingSweepInterval = TimeSpan.FromMilliseconds(50)
        };
    }

    async Task<Guid> SeedPendingAsync(ServiceProvider provider, string name, string content, int attempts = 0)
    {
        await File.WriteAllTextAsync(Path.Combine(root, name), content);
        var metadata = await new LocalFileContentSource("local", root).GetMetadataAsync(name, CancellationToken.None);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var now = DateTimeOffset.UtcNow;
        var torrent = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = metadata.Path,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            ContentLastModified = metadata.LastModified,
            Status = TorrentStatus.Pending,
            Attempts = attempts,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Torrents.Add(torrent);
        await db.SaveChangesAsync();
        return torrent.Id;
    }

    static async Task WaitUntilAsync(ServiceProvider provider, Func<IcecoldDbContext, Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                if (await condition(scope.ServiceProvider.GetRequiredService<IcecoldDbContext>()))
                    return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not met before the timeout.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    // Simulates a transient database failure (e.g. a command timeout) when finalizing a torrent.
    sealed class FailReadyTransitionInterceptor(int failures) : SaveChangesInterceptor
    {
        int remaining = failures;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var finalizing = eventData.Context!.ChangeTracker.Entries<TorrentRecord>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == TorrentStatus.Ready);
            if (finalizing && Interlocked.Decrement(ref remaining) >= 0)
                throw new TimeoutException("Simulated database timeout.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
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
