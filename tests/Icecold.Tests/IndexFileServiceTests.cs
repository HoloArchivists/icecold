using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Icecold.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class IndexFileServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public IndexFileServiceTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Accepted_File_Enqueue_Does_Not_Use_Request_Cancellation_Token()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        var dbOptions = new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        await using var db = new IcecoldDbContext(dbOptions);
        var queue = new RecordingIndexingQueue();
        var service = new IndexFileService(CreateRegistry(), db, queue);
        using var requestCancellation = new CancellationTokenSource();

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "sample.txt", null),
            requestCancellation.Token);

        Assert.Equal(IndexFileSubmissionStatus.Accepted, result.Status);
        Assert.Equal(result.Torrent!.Id, queue.EnqueuedIds.Single());
        Assert.False(queue.LastCancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task Blank_Display_Name_Falls_Back_To_Metadata_Display_Name()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        var dbOptions = new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        await using var db = new IcecoldDbContext(dbOptions);
        var service = new IndexFileService(CreateRegistry(), db, new RecordingIndexingQueue());

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "sample.txt", "   "),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.Accepted, result.Status);
        Assert.Equal("sample.txt", result.Torrent!.DisplayName);
        Assert.Equal("sample.txt", (await db.Torrents.SingleAsync()).DisplayName);
    }

    ContentSourceRegistry CreateRegistry()
        => new(
            Options.Create(new IcecoldOptions
            {
                ContentSources =
                [
                    new ContentSourceOptions
                    {
                        Name = "local",
                        Type = "local",
                        RootPath = root
                    }
                ]
            }),
            new TestWebHostEnvironment(root));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    sealed class RecordingIndexingQueue : IIndexingQueue
    {
        public List<Guid> EnqueuedIds { get; } = [];

        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask EnqueueAsync(Guid torrentId, CancellationToken cancellationToken)
        {
            EnqueuedIds.Add(torrentId);
            LastCancellationToken = cancellationToken;
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
