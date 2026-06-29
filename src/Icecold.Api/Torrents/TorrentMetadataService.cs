using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Torrents;

public sealed class TorrentMetadataService(IcecoldDbContext db, PublicUrlBuilder urls)
{
    public async Task<TorrentFileResult?> GetTorrentFileAsync(string infoHash, CancellationToken cancellationToken)
    {
        var torrent = await GetReadyTorrentAsync(infoHash, cancellationToken);
        if (torrent?.TorrentBytes is null)
            return null;

        return new TorrentFileResult($"{torrent.DisplayName}.torrent", torrent.TorrentBytes);
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
}

public sealed record TorrentFileResult(string FileName, byte[] Bytes);
