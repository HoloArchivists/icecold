using Icecold.Api.Content;

namespace Icecold.Api.Torrents;

// Maps a torrent's byte space onto its backing files. Multi-file torrents pad every file but the
// last up to a piece boundary with a BEP 47 pad file, so a piece never spans two backing files
// and pad bytes (always zero) never need to be read from storage.
public sealed class TorrentContentLayout
{
    const int MaxConcurrentStats = 8;

    readonly long[] offsets;

    TorrentContentLayout(IReadOnlyList<TorrentContentFile> files, long totalLength)
    {
        Files = files;
        TotalLength = totalLength;
        offsets = files.Select(f => f.Offset).ToArray();
    }

    public IReadOnlyList<TorrentContentFile> Files { get; }

    // Length of the torrent's byte space, padding included.
    public long TotalLength { get; }

    public bool IsMultiFile => Files.Count != 1 || Files[0].TorrentPath is not null;

    public static TorrentContentLayout SingleFile(string sourcePath, long length, string? version)
        => new([new TorrentContentFile(0, sourcePath, null, 0, length, version)], length);

    public static TorrentContentLayout MultiFile(IReadOnlyList<TorrentContentFileSpec> files, int pieceLength)
    {
        if (files.Count == 0)
            throw new ArgumentException("A multi-file torrent needs at least one file.", nameof(files));

        var laidOut = new List<TorrentContentFile>(files.Count);
        long offset = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var spec = files[i];
            laidOut.Add(new TorrentContentFile(i, spec.SourcePath, spec.TorrentPath, offset, spec.Length, spec.Version));
            offset += spec.Length;
            if (i < files.Count - 1)
                offset += PaddingAfter(spec.Length, pieceLength);
        }

        return new TorrentContentLayout(laidOut, offset);
    }

    public static long PaddingAfter(long length, int pieceLength)
    {
        var remainder = length % pieceLength;
        return remainder == 0 ? 0 : pieceLength - remainder;
    }

    // Re-stats every file and returns the first whose length or version no longer matches.
    // Content access failures propagate.
    public async Task<TorrentContentFile?> FindChangedFileAsync(IContentSource source, CancellationToken cancellationToken)
    {
        if (Files.Count == 1)
            return await HasChangedAsync(Files[0]) ? Files[0] : null;

        var changed = new bool[Files.Count];
        await Parallel.ForAsync(
            0,
            Files.Count,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStats, CancellationToken = cancellationToken },
            async (i, _) => changed[i] = await HasChangedAsync(Files[i]));

        var index = Array.IndexOf(changed, true);
        return index < 0 ? null : Files[index];

        async Task<bool> HasChangedAsync(TorrentContentFile file)
        {
            var metadata = await source.GetMetadataAsync(file.SourcePath, cancellationToken);
            return metadata.Length != file.Length || metadata.Version != file.Version;
        }
    }

    // The file whose bytes contain offset, or null when offset falls in padding.
    public TorrentContentFile? FindFile(long offset)
    {
        if (offset < 0 || offset >= TotalLength)
            return null;

        var index = Array.BinarySearch(offsets, offset);
        if (index < 0)
            index = ~index - 1;

        var file = Files[index];
        return offset < file.Offset + file.Length ? file : null;
    }

    // Splits [offset, offset + length) into reads of backing files and runs of padding.
    public IEnumerable<TorrentContentSegment> Segments(long offset, long length)
    {
        var position = offset;
        var end = offset + length;
        while (position < end)
        {
            var file = FindFile(position);
            if (file is not null)
            {
                var count = Math.Min(end, file.Offset + file.Length) - position;
                yield return new TorrentContentSegment(file, position - file.Offset, count);
                position += count;
                continue;
            }

            var next = Files.FirstOrDefault(f => f.Offset > position);
            var padEnd = Math.Min(end, next?.Offset ?? TotalLength);
            if (padEnd <= position)
                throw new ArgumentOutOfRangeException(nameof(length), "Range extends beyond the torrent content.");

            yield return new TorrentContentSegment(null, 0, padEnd - position);
            position = padEnd;
        }
    }
}

public sealed record TorrentContentFileSpec(string SourcePath, string TorrentPath, long Length, string? Version);

// TorrentPath is null for a single-file torrent. Offset is the file's position in the torrent's byte space.
public sealed record TorrentContentFile(int Index, string SourcePath, string? TorrentPath, long Offset, long Length, string? Version);

// File is null for padding; FileOffset is relative to the start of File.
public readonly record struct TorrentContentSegment(TorrentContentFile? File, long FileOffset, long Length);
