using System.Buffers;

namespace Icecold.Api.Content;

public sealed class LocalFileContentSource : ISeekableContentSource
{
    readonly string rootPath;

    public LocalFileContentSource(string name, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Source name is required.", nameof(name));

        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is required.", nameof(rootPath));

        Name = name;
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public string Name { get; }

    public bool SupportsEfficientRangeReads => true;

    public Task<ContentMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveExistingFile(path);
        var file = new FileInfo(fullPath);

        var relativePath = ToRelativePath(fullPath);
        var version = $"{file.Length:x16}-{file.LastWriteTimeUtc.Ticks:x16}";
        return Task.FromResult(new ContentMetadata(
            Name,
            relativePath,
            file.Name,
            file.Length,
            version,
            file.LastWriteTimeUtc,
            SupportsEfficientRangeReads));
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveExistingFile(path);
        return Task.FromResult<Stream>(OpenFile(fullPath, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var fullPath = ResolveExistingFile(path);
        var stream = OpenFile(fullPath, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (offset > stream.Length)
        {
            stream.Dispose();
            throw new ContentSourceException($"Range starts beyond the end of '{path}'.");
        }

        stream.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(new BoundedReadStream(stream, length));
    }

    public Task<Stream> OpenSeekableReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveExistingFile(path);
        return Task.FromResult<Stream>(OpenFile(fullPath, FileOptions.Asynchronous | FileOptions.RandomAccess));
    }

    string ResolveExistingFile(string path)
    {
        var fullPath = ResolveFullPath(path);
        if (!File.Exists(fullPath))
            throw new ContentItemNotFoundException(Name, path);

        RejectSymlinkSegments(fullPath);
        return fullPath;
    }

    string ResolveFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ContentSourceException("Path is required.");

        var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            throw new ContentSourceException("Content source paths must be relative.");

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
        if (!IsUnderRoot(fullPath))
            throw new ContentSourceException("Path escapes the configured content source root.");

        return fullPath;
    }

    bool IsUnderRoot(string fullPath)
    {
        var root = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }

    void RejectSymlinkSegments(string fullPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        var current = rootPath;
        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ContentSourceException("Symlinks are not allowed in local content source paths.");
        }
    }

    string ToRelativePath(string fullPath)
        => Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    static FileStream OpenFile(string path, FileOptions options)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, options);
}

sealed class BoundedReadStream(Stream inner, long remaining) : Stream
{
    long remainingBytes = remaining;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => remainingBytes;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (remainingBytes == 0)
            return 0;

        var allowed = (int)Math.Min(count, remainingBytes);
        var read = inner.Read(buffer, offset, allowed);
        remainingBytes -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (remainingBytes == 0)
            return 0;

        var allowed = (int)Math.Min(buffer.Length, remainingBytes);
        var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
        remainingBytes -= read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
