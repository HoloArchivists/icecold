using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Icecold.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Icecold.Tests;

public sealed class IndexFileEndpointTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public IndexFileEndpointTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Index_File_Is_Idempotent_For_Pending_Record()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var first = await PostIndexAsync(client, "sample.txt");
        var second = await PostIndexAsync(client, "./sample.txt");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await ReadIdAsync(first), await ReadIdAsync(second));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        Assert.Equal(1, await db.Torrents.CountAsync());
    }

    [Fact]
    public async Task Index_File_Retries_Failed_Record_Without_New_Row()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        await using var factory = CreateFactory();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
            db.Torrents.Add(new TorrentRecord
            {
                Id = Guid.NewGuid(),
                SourceName = "local",
                SourcePath = "sample.txt",
                DisplayName = "sample.txt",
                ContentLength = 5,
                ContentVersion = new FileInfo(Path.Combine(root, "sample.txt")).Length.ToString("x16") + "-" + File.GetLastWriteTimeUtc(Path.Combine(root, "sample.txt")).Ticks.ToString("x16"),
                Status = TorrentStatus.Failed,
                Error = "previous failure",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await PostIndexAsync(client, "sample.txt");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await assertDb.Torrents.SingleAsync();
        Assert.Equal(TorrentStatus.Pending, torrent.Status);
        Assert.Null(torrent.Error);
    }

    [Fact]
    public async Task Index_File_Marks_Duplicate_InfoHash_As_Alias()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "first.bin"), "same bytes");
        await File.WriteAllTextAsync(Path.Combine(root, "second.bin"), "same bytes");
        await using var factory = CreateFactory(removeHostedServices: false);
        using var client = factory.CreateClient();

        var firstResponse = await PostIndexAsync(client, "first.bin", "shared.bin");
        var firstId = await ReadIdAsync(firstResponse);
        var firstReady = await PollStatusAsync(client, firstId, "Ready");

        var secondResponse = await PostIndexAsync(client, "second.bin", "shared.bin");
        var secondId = await ReadIdAsync(secondResponse);
        var secondDuplicate = await PollStatusAsync(client, secondId, "Duplicate");

        Assert.Equal(firstReady.GetProperty("infoHash").GetString(), secondDuplicate.GetProperty("infoHash").GetString());
        Assert.Equal(firstId, secondDuplicate.GetProperty("duplicateOfId").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        Assert.Equal(1, await db.Torrents.CountAsync(t => t.Status == TorrentStatus.Ready));
        Assert.Equal(1, await db.Torrents.CountAsync(t => t.Status == TorrentStatus.Duplicate));
    }

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
                    ["Icecold:ContentSources:0:RootPath"] = root
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

    static HttpRequestMessage CreateIndexRequest(string path, string? displayName = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/file")
        {
            Content = JsonContent.Create(new { source = "local", path, displayName })
        };
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
        return request;
    }

    static async Task<HttpResponseMessage> PostIndexAsync(HttpClient client, string path, string? displayName = null)
    {
        using var request = CreateIndexRequest(path, displayName);
        return await client.SendAsync(request, CancellationToken.None);
    }

    static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    static async Task<JsonElement> PollStatusAsync(HttpClient client, string id, string expectedStatus)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/torrents/{id}");
            request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");
            using var response = await client.SendAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
            var root = document.RootElement.Clone();
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
