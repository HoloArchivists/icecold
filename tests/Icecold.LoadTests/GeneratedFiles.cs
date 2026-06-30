using System.Buffers;
using System.Diagnostics;

namespace Icecold.LoadTests;

static class GeneratedFiles
{
    public static async Task<FileFixture> EnsureAsync(
        string contentRoot,
        string relativePath,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var stopwatch = Stopwatch.StartNew();
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != sizeBytes)
            await WriteDeterministicFileAsync(fullPath, sizeBytes, cancellationToken);

        stopwatch.Stop();
        return new FileFixture(relativePath.Replace(Path.DirectorySeparatorChar, '/'), sizeBytes, stopwatch.Elapsed);
    }

    public static async Task<IReadOnlyList<FileFixture>> CreateManyAsync(
        string contentRoot,
        string prefix,
        int count,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var files = new List<FileFixture>(count);
        for (var i = 0; i < count; i++)
        {
            var relativePath = $"{prefix}/file-{i:D8}.bin";
            files.Add(await EnsureAsync(contentRoot, relativePath, sizeBytes, cancellationToken));
        }

        return files;
    }

    static async Task WriteDeterministicFileAsync(string fullPath, long sizeBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var random = new Random(HashCode.Combine(fullPath, sizeBytes));
            await using var stream = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var remaining = sizeBytes;
            while (remaining > 0)
            {
                var length = (int)Math.Min(buffer.Length, remaining);
                random.NextBytes(buffer.AsSpan(0, length));
                await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                remaining -= length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

sealed record FileFixture(string RelativePath, long SizeBytes, TimeSpan CreateElapsed);
