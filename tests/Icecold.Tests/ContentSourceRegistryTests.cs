using Icecold.Api.Content;
using Icecold.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class ContentSourceRegistryTests : IDisposable
{
    readonly string contentRoot = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public ContentSourceRegistryTests()
    {
        Directory.CreateDirectory(contentRoot);
    }

    [Fact]
    public void Configured_Local_Source_Requires_RootPath()
    {
        var options = Options.Create(new IcecoldOptions
        {
            ContentSources =
            [
                new ContentSourceOptions
                {
                    Name = "local",
                    Type = "local",
                    RootPath = ""
                }
            ]
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ContentSourceRegistry(options, new TestWebHostEnvironment(contentRoot)));

        Assert.Equal("Local content source 'local' must configure RootPath.", exception.Message);
    }

    [Fact]
    public async Task Empty_Source_List_Uses_Default_Data_Files_Root()
    {
        var dataRoot = Path.Combine(contentRoot, "data", "files");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "sample.txt"), "hello");

        var registry = new ContentSourceRegistry(
            Options.Create(new IcecoldOptions()),
            new TestWebHostEnvironment(contentRoot));

        var metadata = await registry.GetRequired("local").GetMetadataAsync("sample.txt", CancellationToken.None);

        Assert.Equal("sample.txt", metadata.Path);
        Assert.Equal(5, metadata.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(contentRoot))
            Directory.Delete(contentRoot, recursive: true);
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
