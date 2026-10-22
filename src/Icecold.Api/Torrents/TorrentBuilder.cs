using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Icecold.Api.Content;

namespace Icecold.Api.Torrents;

public sealed class TorrentBuilder(PublicUrlBuilder urls)
{
    const string CreatedBy = "Icecold";

    public async Task<TorrentBuildResult> BuildSingleFileAsync(
        ContentMetadata metadata,
        IContentSource source,
        string? displayName,
        CancellationToken cancellationToken,
        Action<long>? onProgress = null)
    {
        // A zero-length file would produce an empty `pieces` string, which clients reject.
        if (metadata.Length <= 0)
            throw new ContentSourceException($"Zero-length file '{metadata.Path}' cannot be indexed.");

        var torrentName = string.IsNullOrWhiteSpace(displayName) ? metadata.DisplayName : displayName;
        var pieceLength = SelectPieceLength(metadata.Length);
        using var hashes = new MemoryStream();
        await HashPiecesAsync(source, metadata, pieceLength, hashes, padFinalPiece: false, 0, onProgress, cancellationToken);

        var info = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["length"] = metadata.Length,
            ["name"] = torrentName,
            ["piece length"] = pieceLength,
            ["pieces"] = hashes.ToArray()
        };

        return Build(info, torrentName, pieceLength, multiFile: false);
    }

    // Files are laid out in the given order, each but the last followed by a BEP 47 pad file up to
    // the next piece boundary (see TorrentContentLayout).
    public async Task<TorrentBuildResult> BuildMultiFileAsync(
        string name,
        IReadOnlyList<MultiFileTorrentEntry> files,
        IContentSource source,
        CancellationToken cancellationToken,
        Action<long>? onProgress = null)
    {
        if (files.Count == 0)
            throw new ContentSourceException("A multi-file torrent needs at least one file.");

        foreach (var file in files)
        {
            if (file.Metadata.Length <= 0)
                throw new ContentSourceException($"Zero-length file '{file.Metadata.Path}' cannot be indexed.");
        }

        var pieceLength = SelectPieceLength(files.Sum(f => f.Metadata.Length));
        using var hashes = new MemoryStream();
        var fileList = new List<object>(files.Count * 2);
        long hashedBytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var isLast = i == files.Count - 1;
            await HashPiecesAsync(source, file.Metadata, pieceLength, hashes, padFinalPiece: !isLast, hashedBytes, onProgress, cancellationToken);
            hashedBytes += file.Metadata.Length;

            fileList.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["length"] = file.Metadata.Length,
                ["path"] = file.TorrentPath.Split('/').Cast<object>().ToList()
            });

            var padding = TorrentContentLayout.PaddingAfter(file.Metadata.Length, pieceLength);
            if (!isLast && padding > 0)
            {
                fileList.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["attr"] = "p",
                    ["length"] = padding,
                    ["path"] = new List<object> { ".pad", padding.ToString(CultureInfo.InvariantCulture) }
                });
            }
        }

        var info = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["files"] = fileList,
            ["name"] = name,
            ["piece length"] = pieceLength,
            ["pieces"] = hashes.ToArray()
        };

        return Build(info, name, pieceLength, multiFile: true);
    }

    TorrentBuildResult Build(Dictionary<string, object> info, string torrentName, int pieceLength, bool multiFile)
    {
        var pieceCount = ((byte[])info["pieces"]).Length / SHA1.HashSizeInBytes;
        var infoBytes = Bencode.Encode(info);
        var infoHash = InfoHashUtil.Sha1Hex(infoBytes);
        var webSeedUrl = urls.WebSeedEnabled ? urls.BuildWebSeedUrl(infoHash, torrentName, multiFile) : null;

        var torrent = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["created by"] = CreatedBy,
            ["info"] = info
        };
        if (urls.TrackerAnnounceUrls.Count > 0)
            torrent["announce"] = urls.TrackerAnnounceUrls[0];

        if (urls.TrackerAnnounceUrls.Count > 1)
            torrent["announce-list"] = BuildAnnounceList(urls.TrackerAnnounceUrls);

        if (webSeedUrl is not null)
            torrent["url-list"] = webSeedUrl;

        return new TorrentBuildResult(
            Bencode.Encode(torrent),
            infoHash,
            pieceLength,
            pieceCount,
            webSeedUrl);
    }

    static IReadOnlyList<object> BuildAnnounceList(IReadOnlyList<string> trackerUrls)
        => trackerUrls
            .Select(url => (object)new List<object> { url })
            .ToList();

    static int SelectPieceLength(long length)
    {
        if (length <= 64L * 1024 * 1024)
            return 256 * 1024;
        if (length <= 1024L * 1024 * 1024)
            return 1024 * 1024;
        if (length <= 16L * 1024 * 1024 * 1024)
            return 4 * 1024 * 1024;
        return 16 * 1024 * 1024;
    }

    // Appends the SHA-1 of each piece of one file to hashes. With padFinalPiece, a short final
    // piece is zero-filled to pieceLength, matching the pad file that follows it.
    static async Task HashPiecesAsync(
        IContentSource source,
        ContentMetadata metadata,
        int pieceLength,
        Stream hashes,
        bool padFinalPiece,
        long progressBase,
        Action<long>? onProgress,
        CancellationToken cancellationToken)
    {
        var path = metadata.Path;
        var expectedLength = metadata.Length;
        await using var content = await source.OpenReadAsync(path, cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(pieceLength);

        try
        {
            long totalRead = 0;
            while (true)
            {
                var pieceBytes = 0;
                while (pieceBytes < pieceLength)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(pieceBytes, pieceLength - pieceBytes), cancellationToken);
                    if (read == 0)
                        break;

                    pieceBytes += read;
                    totalRead += read;
                }

                if (pieceBytes == 0)
                    break;

                if (padFinalPiece && pieceBytes < pieceLength)
                {
                    buffer.AsSpan(pieceBytes, pieceLength - pieceBytes).Clear();
                    pieceBytes = pieceLength;
                }

                var hash = SHA1.HashData(buffer.AsSpan(0, pieceBytes));
                hashes.Write(hash);
                onProgress?.Invoke(progressBase + totalRead);
            }

            if (totalRead != expectedLength)
                throw new ContentSourceException($"Content length changed while hashing '{path}'. Expected {expectedLength} bytes, read {totalRead} bytes.");

            // Same-length rewrites are only visible through the modification time. Re-stat the
            // handle that was hashed so the hashes are known to match metadata.Version.
            var versionAfterHashing = source.GetOpenStreamVersion(content);
            if (versionAfterHashing is not null && versionAfterHashing != metadata.Version)
                throw new ContentSourceException($"Content changed while hashing '{path}'.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

// TorrentPath is slash-separated and relative to the torrent name.
public sealed record MultiFileTorrentEntry(ContentMetadata Metadata, string TorrentPath);

public sealed record TorrentBuildResult(
    byte[] TorrentBytes,
    string InfoHashHex,
    int PieceLength,
    int PieceCount,
    string? WebSeedUrl);
