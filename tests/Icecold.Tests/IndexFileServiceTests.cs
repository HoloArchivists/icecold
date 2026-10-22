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
    public async Task Accepted_File_Is_Queued_And_Wakes_A_Worker()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var db = CreateDb();
        var signal = new IndexingSignal();
        var service = new IndexFileService(CreateRegistry(), db, signal);

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "sample.txt", null, Priority: 7),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.Accepted, result.Status);
        var stored = await db.Torrents.SingleAsync();
        Assert.Equal(TorrentStatus.Pending, stored.Status);
        Assert.Equal(7, stored.Priority);
        Assert.NotEqual(default, stored.AvailableAt);
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Resubmitting_A_Queued_File_Raises_But_Never_Lowers_Priority()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());

        await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null, Priority: 5), CancellationToken.None);
        await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null, Priority: 2), CancellationToken.None);
        Assert.Equal(5, (await db.Torrents.SingleAsync()).Priority);

        await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null), CancellationToken.None);
        Assert.Equal(5, (await db.Torrents.SingleAsync()).Priority);

        var raised = await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null, Priority: 50), CancellationToken.None);
        Assert.Equal(IndexFileSubmissionStatus.Accepted, raised.Status);
        Assert.Equal(50, (await db.Torrents.SingleAsync()).Priority);
    }

    [Fact]
    public async Task Blank_Display_Name_Falls_Back_To_Metadata_Display_Name()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "sample.txt", "   "),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.Accepted, result.Status);
        Assert.Equal("sample.txt", result.Torrent!.DisplayName);
        Assert.Equal("sample.txt", (await db.Torrents.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task Too_Long_Display_Name_Is_Bad_Request()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "sample.txt", new string('n', 513)),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.BadRequest, result.Status);
        Assert.Empty(db.Torrents);
    }

    [Fact]
    public async Task Too_Long_Path_Is_Bad_Request()
    {
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", new string('p', 4097), null),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Zero_Length_File_Is_Bad_Request()
    {
        await File.WriteAllBytesAsync(Path.Combine(root, "empty.bin"), []);
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());

        var result = await service.SubmitFileAsync(
            new IndexFileCommand("local", "empty.bin", null),
            CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.BadRequest, result.Status);
        Assert.Empty(db.Torrents);
    }

    [Fact]
    public async Task Resubmitting_Failed_Record_Resets_Attempts()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var db = CreateDb();
        var service = new IndexFileService(CreateRegistry(), db, new IndexingSignal());
        var first = await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null), CancellationToken.None);
        var stored = await db.Torrents.SingleAsync(t => t.Id == first.Torrent!.Id);
        stored.Status = TorrentStatus.Failed;
        stored.Attempts = 5;
        await db.SaveChangesAsync();

        var second = await service.SubmitFileAsync(new IndexFileCommand("local", "sample.txt", null), CancellationToken.None);

        Assert.Equal(IndexFileSubmissionStatus.Accepted, second.Status);
        Assert.Equal(TorrentStatus.Pending, stored.Status);
        Assert.Equal(0, stored.Attempts);
    }

    static IcecoldDbContext CreateDb()
        => new(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

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
