using System.Buffers;
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
        CancellationToken cancellationToken)
    {
        var torrentName = string.IsNullOrWhiteSpace(displayName) ? metadata.DisplayName : displayName;
        var pieceLength = SelectPieceLength(metadata.Length);
        var pieces = await HashPiecesAsync(source, metadata.Path, metadata.Length, pieceLength, cancellationToken);
        var pieceCount = pieces.Length / SHA1.HashSizeInBytes;

        var info = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["length"] = metadata.Length,
            ["name"] = torrentName,
            ["piece length"] = pieceLength,
            ["pieces"] = pieces
        };

        var infoBytes = Bencode.Encode(info);
        var infoHash = InfoHashUtil.Sha1Hex(infoBytes);
        var webSeedUrl = urls.BuildWebSeedUrl(infoHash, torrentName);

        var torrent = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["announce"] = urls.TrackerAnnounceUrl,
            ["created by"] = CreatedBy,
            ["info"] = info,
            ["url-list"] = webSeedUrl
        };

        return new TorrentBuildResult(
            Bencode.Encode(torrent),
            infoHash,
            pieceLength,
            pieceCount,
            webSeedUrl);
    }

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

    static async Task<byte[]> HashPiecesAsync(
        IContentSource source,
        string path,
        long expectedLength,
        int pieceLength,
        CancellationToken cancellationToken)
    {
        await using var content = await source.OpenReadAsync(path, cancellationToken);
        using var hashes = new MemoryStream();
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

                var hash = SHA1.HashData(buffer.AsSpan(0, pieceBytes));
                hashes.Write(hash);
            }

            if (totalRead != expectedLength)
                throw new ContentSourceException($"Content length changed while hashing '{path}'. Expected {expectedLength} bytes, read {totalRead} bytes.");

            return hashes.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed record TorrentBuildResult(
    byte[] TorrentBytes,
    string InfoHashHex,
    int PieceLength,
    int PieceCount,
    string WebSeedUrl);
