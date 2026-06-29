namespace Icecold.Api.Content;

public interface IContentSource
{
    string Name { get; }

    bool SupportsEfficientRangeReads { get; }

    Task<ContentMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken cancellationToken);
}

public interface ISeekableContentSource : IContentSource
{
    Task<Stream> OpenSeekableReadAsync(string path, CancellationToken cancellationToken);
}

public sealed record ContentMetadata(
    string SourceName,
    string Path,
    string DisplayName,
    long Length,
    string? Version,
    DateTimeOffset? LastModified,
    bool SupportsEfficientRangeReads);
