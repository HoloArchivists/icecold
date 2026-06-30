using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;
using MonoTorrent.BEncoding;

namespace Icecold.Api.Torrents;

public sealed class TorrentMetadataService(IcecoldDbContext db, PublicUrlBuilder urls)
{
    public async Task<TorrentFileResult?> GetTorrentFileAsync(string infoHash, CancellationToken cancellationToken)
    {
        var torrent = await GetReadyTorrentAsync(infoHash, cancellationToken);
        if (torrent?.TorrentBytes is null)
            return null;

        return new TorrentFileResult($"{torrent.DisplayName}.torrent", NormalizeTorrentBytes(torrent));
    }

    public async Task<string?> GetMagnetAsync(string infoHash, CancellationToken cancellationToken)
    {
        var torrent = await GetReadyTorrentAsync(infoHash, cancellationToken);
        return torrent is null ? null : urls.BuildMagnet(torrent);
    }

    async Task<TorrentRecord?> GetReadyTorrentAsync(string infoHash, CancellationToken cancellationToken)
    {
        var normalized = infoHash.ToLowerInvariant();
        return await db.Torrents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.InfoHashHex == normalized && t.Status == TorrentStatus.Ready, cancellationToken);
    }

    byte[] NormalizeTorrentBytes(TorrentRecord torrent)
    {
        var decoded = BEncodedDictionary.DecodeTorrent(torrent.TorrentBytes!).torrent;
        var webSeedKey = new BEncodedString("url-list");

        if (urls.WebSeedEnabled)
            decoded[webSeedKey] = new BEncodedString(urls.BuildWebSeedUrl(torrent.InfoHashHex!, torrent.DisplayName));
        else
            decoded.Remove(webSeedKey);

        return decoded.Encode();
    }
}

public sealed record TorrentFileResult(string FileName, byte[] Bytes);
