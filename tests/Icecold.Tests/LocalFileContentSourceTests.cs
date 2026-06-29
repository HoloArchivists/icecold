using Icecold.Api.Content;

namespace Icecold.Tests;

public sealed class LocalFileContentSourceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));
    readonly string outsideRoot = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public LocalFileContentSourceTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
    }

    [Fact]
    public async Task GetMetadata_Normalizes_Relative_Path()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "hello");
        var source = new LocalFileContentSource("local", root);

        var metadata = await source.GetMetadataAsync("sub/../sample.txt", CancellationToken.None);

        Assert.Equal("local", metadata.SourceName);
        Assert.Equal("sample.txt", metadata.Path);
        Assert.Equal("sample.txt", metadata.DisplayName);
        Assert.Equal(5, metadata.Length);
    }

    [Fact]
    public async Task GetMetadata_Blocks_Path_Traversal()
    {
        var source = new LocalFileContentSource("local", root);

        await Assert.ThrowsAsync<ContentSourceException>(() =>
            source.GetMetadataAsync("../outside.txt", CancellationToken.None));
    }

    [Fact]
    public async Task OpenRangeAsync_Returns_Requested_Bytes()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "abcdef");
        var source = new LocalFileContentSource("local", root);

        await using var stream = await source.OpenRangeAsync("sample.txt", 2, 3, CancellationToken.None);
        using var reader = new StreamReader(stream);

        Assert.Equal("cde", await reader.ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetMetadata_Blocks_Symlink_To_File_Outside_Root()
    {
        var outsideFile = Path.Combine(outsideRoot, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "secret");
        var linkPath = Path.Combine(root, "link.txt");

        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var source = new LocalFileContentSource("local", root);

        await Assert.ThrowsAsync<ContentSourceException>(() =>
            source.GetMetadataAsync("link.txt", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        if (Directory.Exists(outsideRoot))
            Directory.Delete(outsideRoot, recursive: true);
    }
}
