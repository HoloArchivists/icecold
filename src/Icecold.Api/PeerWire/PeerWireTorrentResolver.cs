using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireTorrentResolver(IServiceScopeFactory scopeFactory, ContentSourceRegistry sources)
{
    public async Task<PeerWireTorrentContext?> ResolveAsync(string infoHashHex, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var normalized = infoHashHex.ToLowerInvariant();
        var torrent = await db.Torrents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.InfoHashHex == normalized && t.Status == TorrentStatus.Ready, cancellationToken);

        return await BuildContextAsync(torrent, normalized, cancellationToken);
    }

    public async Task<PeerWireTorrentContext?> ResolveByMseObfuscatedHashAsync(string obfuscatedHashHex, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var normalized = obfuscatedHashHex.ToLowerInvariant();
        var torrent = await db.Torrents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.MseObfuscatedHashHex == normalized && t.Status == TorrentStatus.Ready, cancellationToken);

        return await BuildContextAsync(torrent, torrent?.InfoHashHex?.ToLowerInvariant(), cancellationToken);
    }

    async Task<PeerWireTorrentContext?> BuildContextAsync(
        TorrentRecord? torrent,
        string? expectedInfoHashHex,
        CancellationToken cancellationToken)
    {
        if (torrent is null || torrent.PieceLength is null || torrent.PieceCount is null || torrent.TorrentBytes is null)
            return null;

        if (!PeerWireBencode.TryExtractTorrentInfo(torrent.TorrentBytes, out var infoBytes))
            return null;

        if (!string.Equals(InfoHashUtil.Sha1Hex(infoBytes), expectedInfoHashHex, StringComparison.Ordinal))
            return null;

        var source = sources.GetRequired(torrent.SourceName);
        var metadata = await source.GetMetadataAsync(torrent.SourcePath, cancellationToken);
        if (metadata.Length != torrent.ContentLength || metadata.Version != torrent.ContentVersion)
            return null;

        return new PeerWireTorrentContext(
            torrent.InfoHashHex!,
            source,
            metadata.Path,
            torrent.ContentLength,
            torrent.PieceLength.Value,
            torrent.PieceCount.Value,
            infoBytes);
    }
}

public sealed record PeerWireTorrentContext(
    string InfoHashHex,
    IContentSource Source,
    string SourcePath,
    long ContentLength,
    int PieceLength,
    int PieceCount,
    byte[] InfoBytes);
