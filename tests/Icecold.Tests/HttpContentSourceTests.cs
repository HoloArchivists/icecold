using System.Net;
using System.Net.Http.Headers;
using Icecold.Api.Content;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class HttpContentSourceTests
{
    [Fact]
    public async Task Metadata_Uses_Strong_ETag_And_Normalized_Path()
    {
        var origin = new FakeOrigin();
        origin.Put("dir/my file #1?.bin", Bytes(100), etag: "\"v1\"");
        using var source = CreateSource(origin);

        var metadata = await source.GetMetadataAsync("dir\\my file #1?.bin", CancellationToken.None);

        Assert.Equal("dir/my file #1?.bin", metadata.Path);
        Assert.Equal("my file #1?.bin", metadata.DisplayName);
        Assert.Equal(100, metadata.Length);
        Assert.Equal($"{100:x16}-e\"v1\"", metadata.Version);
        Assert.True(metadata.SupportsEfficientRangeReads);
        Assert.Equal("/base/dir/my%20file%20%231%3F.bin", origin.Requests.Single().Url.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../secret.bin")]
    [InlineData("dir/../../secret.bin")]
    [InlineData("dir//file.bin")]
    [InlineData("./file.bin")]
    [InlineData("http://evil.test/x")]
    public async Task Unsafe_Paths_Are_Rejected_Before_Any_Request(string path)
    {
        var origin = new FakeOrigin();
        using var source = CreateSource(origin);

        await Assert.ThrowsAsync<ContentSourceException>(() => source.GetMetadataAsync(path, CancellationToken.None));
        Assert.Empty(origin.Requests);
    }

    [Fact]
    public async Task Status_Codes_Map_To_Content_Source_Errors()
    {
        var origin = new FakeOrigin();
        using var source = CreateSource(origin);

        await Assert.ThrowsAsync<ContentItemNotFoundException>(() => source.GetMetadataAsync("missing.bin", CancellationToken.None));

        origin.StatusOverride = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ContentSourceUnavailableException>(() => source.GetMetadataAsync("any.bin", CancellationToken.None));

        origin.StatusOverride = HttpStatusCode.Found;
        var redirect = await Assert.ThrowsAsync<ContentSourceException>(() => source.GetMetadataAsync("any.bin", CancellationToken.None));
        Assert.Contains("redirects are not followed", redirect.Message);

        origin.StatusOverride = HttpStatusCode.Forbidden;
        Assert.IsNotType<ContentSourceUnavailableException>(
            await Assert.ThrowsAnyAsync<Exception>(() => source.GetMetadataAsync("any.bin", CancellationToken.None)));
    }

    [Fact]
    public async Task Metadata_Falls_Back_To_Range_Probe_When_Head_Is_Rejected()
    {
        var origin = new FakeOrigin { AllowHead = false };
        origin.Put("file.bin", Bytes(50), etag: "\"v1\"");
        using var source = CreateSource(origin);

        var metadata = await source.GetMetadataAsync("file.bin", CancellationToken.None);

        Assert.Equal(50, metadata.Length);
        Assert.Equal(HttpMethod.Get, origin.Requests[^1].Method);
        Assert.Equal("bytes=0-0", origin.Requests[^1].Range);
    }

    [Fact]
    public async Task Origin_Without_Validators_Is_Rejected()
    {
        var origin = new FakeOrigin();
        origin.Put("file.bin", Bytes(10), etag: null, lastModified: null);
        using var source = CreateSource(origin);

        var exception = await Assert.ThrowsAsync<ContentSourceException>(() => source.GetMetadataAsync("file.bin", CancellationToken.None));
        Assert.Contains("ETag", exception.Message);
    }

    [Fact]
    public async Task Weak_ETag_Falls_Back_To_Last_Modified_Precondition()
    {
        var origin = new FakeOrigin();
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        origin.Put("file.bin", Bytes(10), etag: "W/\"weak\"", lastModified: modified);
        using var source = CreateSource(origin);

        var metadata = await source.GetMetadataAsync("file.bin", CancellationToken.None);
        await using var stream = await source.OpenRangeAsync("file.bin", 2, 4, CancellationToken.None);
        await ReadAllAsync(stream);

        Assert.Contains("-m", metadata.Version);
        Assert.Equal(modified, origin.Requests[^1].IfUnmodifiedSince);
    }

    [Fact]
    public async Task Range_Read_Returns_Requested_Bytes_Pinned_To_Metadata_Version()
    {
        var origin = new FakeOrigin();
        var bytes = Bytes(1000);
        origin.Put("file.bin", bytes, etag: "\"v1\"");
        using var source = CreateSource(origin);

        await source.GetMetadataAsync("file.bin", CancellationToken.None);
        await using var stream = await source.OpenRangeAsync("file.bin", 100, 250, CancellationToken.None);

        Assert.Equal(bytes[100..350], await ReadAllAsync(stream));
        Assert.Equal("bytes=100-349", origin.Requests[^1].Range);
        Assert.Equal("\"v1\"", origin.Requests[^1].IfMatch);
    }

    [Fact]
    public async Task Content_Replaced_After_Metadata_Fails_Reads_And_Refreshes_Metadata()
    {
        var origin = new FakeOrigin();
        origin.Put("file.bin", Bytes(100), etag: "\"v1\"");
        using var source = CreateSource(origin);
        var before = await source.GetMetadataAsync("file.bin", CancellationToken.None);

        origin.Put("file.bin", Bytes(100, seed: 7), etag: "\"v2\"");
        var exception = await Assert.ThrowsAsync<ContentSourceException>(
            () => source.OpenRangeAsync("file.bin", 0, 10, CancellationToken.None));

        Assert.Contains("changed", exception.Message);
        var after = await source.GetMetadataAsync("file.bin", CancellationToken.None);
        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public async Task Origin_Ignoring_Preconditions_Is_Caught_By_Response_Validators()
    {
        var origin = new FakeOrigin { IgnorePreconditions = true };
        origin.Put("file.bin", Bytes(100), etag: "\"v1\"");
        using var source = CreateSource(origin);
        await source.GetMetadataAsync("file.bin", CancellationToken.None);

        origin.Put("file.bin", Bytes(100, seed: 7), etag: "\"v2\"");

        await Assert.ThrowsAsync<ContentSourceException>(() => source.OpenRangeAsync("file.bin", 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Dropped_Connection_Is_Resumed_With_A_Pinned_Range_Request()
    {
        var origin = new FakeOrigin { FailBodyOnceAfterBytes = 300 };
        var bytes = Bytes(1000);
        origin.Put("file.bin", bytes, etag: "\"v1\"");
        using var source = CreateSource(origin);

        await using var stream = await source.OpenReadAsync("file.bin", CancellationToken.None);

        Assert.Equal(bytes, await ReadAllAsync(stream));
        var resume = origin.Requests[^1];
        Assert.Equal("bytes=300-999", resume.Range);
        Assert.Equal("\"v1\"", resume.IfMatch);
        Assert.Equal($"{1000:x16}-e\"v1\"", source.GetOpenStreamVersion(stream));
    }

    [Fact]
    public async Task Dropped_Connection_Without_Range_Support_Fails()
    {
        var origin = new FakeOrigin { FailBodyOnceAfterBytes = 300, SupportsRanges = false };
        origin.Put("file.bin", Bytes(1000), etag: "\"v1\"");
        using var source = CreateSource(origin);

        await using var stream = await source.OpenReadAsync("file.bin", CancellationToken.None);

        await Assert.ThrowsAnyAsync<IOException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task Origin_Ignoring_Range_Is_Rejected_For_Offset_Reads()
    {
        var origin = new FakeOrigin { SupportsRanges = false };
        origin.Put("file.bin", Bytes(1000), etag: "\"v1\"");
        using var source = CreateSource(origin);

        var exception = await Assert.ThrowsAsync<ContentSourceException>(
            () => source.OpenRangeAsync("file.bin", 500, 10, CancellationToken.None));
        Assert.Contains("range requests", exception.Message);
    }

    [Fact]
    public async Task Seekable_Stream_Serves_Blocks_From_Read_Ahead_Windows()
    {
        var origin = new FakeOrigin();
        var bytes = Bytes(100_000);
        origin.Put("file.bin", bytes, etag: "\"v1\"");
        using var source = CreateSource(origin, readAheadBytes: 32 * 1024);

        await using var stream = await source.OpenSeekableReadAsync("file.bin", CancellationToken.None);
        var requestsAfterOpen = origin.Requests.Count;
        var block = new byte[4096];
        for (var offset = 0; offset + block.Length <= bytes.Length; offset += block.Length)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(block);
            Assert.Equal(bytes.AsSpan(offset, block.Length).ToArray(), block);
        }

        // 24 blocks of 4 KiB fit in 3 windows of 32 KiB.
        Assert.Equal(3, origin.Requests.Count - requestsAfterOpen);
    }

    [Fact]
    public async Task Static_File_Origin_Hashes_To_The_Same_Torrent_As_Local_Source()
    {
        var root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "sub dir"));
        try
        {
            var bytes = Bytes(3 * 256 * 1024 + 123);
            await File.WriteAllBytesAsync(Path.Combine(root, "sub dir", "payload.bin"), bytes);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(root),
                RequestPath = "/base",
                ServeUnknownFileTypes = true
            });
            await app.StartAsync();

            using var http = CreateSource(app.GetTestServer().CreateHandler());
            var local = new LocalFileContentSource("local", root);
            var torrents = new TorrentBuilder(new PublicUrlBuilder(Options.Create(new IcecoldOptions { PublicBaseUrl = "http://example.test" })));

            var httpMetadata = await http.GetMetadataAsync("sub dir/payload.bin", CancellationToken.None);
            var localMetadata = await local.GetMetadataAsync("sub dir/payload.bin", CancellationToken.None);
            var progress = new List<long>();
            var fromHttp = await torrents.BuildSingleFileAsync(httpMetadata, http, null, CancellationToken.None, progress.Add);
            var fromLocal = await torrents.BuildSingleFileAsync(localMetadata, local, null, CancellationToken.None);

            Assert.Equal(fromLocal.InfoHashHex, fromHttp.InfoHashHex);
            Assert.Equal(bytes.Length, progress[^1]);

            await using var range = await http.OpenRangeAsync("sub dir/payload.bin", 1000, 5000, CancellationToken.None);
            Assert.Equal(bytes[1000..6000], await ReadAllAsync(range));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_Builds_Http_Sources_And_Validates_Base_Url()
    {
        using var registry = new ContentSourceRegistry(
            Options.Create(new IcecoldOptions
            {
                ContentSources = [new ContentSourceOptions { Name = "mirror", Type = "http", BaseUrl = "https://mirror.example/files" }]
            }),
            new NullEnvironment());
        Assert.IsType<HttpContentSource>(registry.GetRequired("mirror"));

        foreach (var baseUrl in new[] { "", "ftp://mirror.example/", "https://user:pw@mirror.example/", "https://mirror.example/?sig=1" })
        {
            Assert.Throws<InvalidOperationException>(() => new ContentSourceRegistry(
                Options.Create(new IcecoldOptions
                {
                    ContentSources = [new ContentSourceOptions { Name = "mirror", Type = "http", BaseUrl = baseUrl }]
                }),
                new NullEnvironment()));
        }
    }

    static HttpContentSource CreateSource(HttpMessageHandler handler, int readAheadBytes = 1024 * 1024)
        => new(
            new ContentSourceOptions
            {
                Name = "mirror",
                Type = "http",
                BaseUrl = "http://origin.test/base",
                MetadataCacheSeconds = 60,
                ReadAheadBytes = readAheadBytes
            },
            handler);

    static byte[] Bytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, 64);
        return buffer.ToArray();
    }

    sealed record RecordedRequest(HttpMethod Method, Uri Url, string? Range, string? IfMatch, DateTimeOffset? IfUnmodifiedSince);

    // Minimal static-file origin with scriptable misbehaviour.
    sealed class FakeOrigin : HttpMessageHandler
    {
        readonly Dictionary<string, (byte[] Bytes, string? ETag, DateTimeOffset? LastModified)> files = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        public bool AllowHead { get; init; } = true;

        public bool SupportsRanges { get; init; } = true;

        public bool IgnorePreconditions { get; init; }

        public int FailBodyOnceAfterBytes { get; set; } = -1;

        public HttpStatusCode? StatusOverride { get; set; }

        public void Put(string path, byte[] bytes, string? etag, DateTimeOffset? lastModified = null)
            => files["/base/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))] = (bytes, etag, lastModified ?? (etag is null ? null : DateTimeOffset.UnixEpoch));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Range?.ToString(),
                request.Headers.IfMatch.FirstOrDefault()?.ToString(),
                request.Headers.IfUnmodifiedSince));

            if (StatusOverride is { } status)
                return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });

            if (request.Method == HttpMethod.Head && !AllowHead)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

            if (!files.TryGetValue(request.RequestUri!.AbsolutePath, out var file))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (!IgnorePreconditions)
            {
                var ifMatch = request.Headers.IfMatch.FirstOrDefault()?.ToString();
                var ifUnmodifiedSince = request.Headers.IfUnmodifiedSince;
                if ((ifMatch is not null && ifMatch != file.ETag)
                    || (ifUnmodifiedSince is not null && file.LastModified > ifUnmodifiedSince))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
                }
            }

            long from = 0;
            var to = file.Bytes.Length - 1L;
            var partial = false;
            if (SupportsRanges && request.Method == HttpMethod.Get && request.Headers.Range?.Ranges.SingleOrDefault() is { } range)
            {
                from = range.From!.Value;
                to = Math.Min(range.To ?? to, to);
                partial = true;
            }

            var body = request.Method == HttpMethod.Head ? [] : file.Bytes[(int)from..(int)(to + 1)];
            HttpContent content = FailBodyOnceAfterBytes >= 0 && request.Method == HttpMethod.Get
                ? new StreamContent(new FailingStream(body, FailBodyOnceAfterBytes))
                : new ByteArrayContent(body);
            FailBodyOnceAfterBytes = -1;

            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentLength = partial ? to - from + 1 : file.Bytes.Length;
            if (partial)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Bytes.Length);
            if (file.ETag is not null)
                response.Headers.ETag = EntityTagHeaderValue.Parse(file.ETag);
            response.Content.Headers.LastModified = file.LastModified;
            if (SupportsRanges)
                response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }

    sealed class FailingStream(byte[] bytes, int failAfter) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAfter)
                throw new IOException("Simulated connection reset.");
            return base.Read(buffer, offset, (int)Math.Min(count, failAfter - Position));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter)
                throw new IOException("Simulated connection reset.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, failAfter - Position)], cancellationToken);
        }
    }

    sealed class NullEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Icecold.Tests";

        public string WebRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
