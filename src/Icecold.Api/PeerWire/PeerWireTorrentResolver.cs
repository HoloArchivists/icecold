using Icecold.Api.Torrents;
using Icecold.Api.Content;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireTorrentResolver(TorrentLocationService locations)
{
    public async Task<PeerWireTorrentContext?> ResolveAsync(string infoHashHex, CancellationToken cancellationToken)
    {
        var normalized = infoHashHex.ToLowerInvariant();
        var resolved = await locations.ResolveByInfoHashAsync(normalized, cancellationToken);
        return BuildContext(resolved, normalized);
    }

    public async Task<PeerWireTorrentContext?> ResolveByMseObfuscatedHashAsync(string obfuscatedHashHex, CancellationToken cancellationToken)
    {
        var normalized = obfuscatedHashHex.ToLowerInvariant();
        var resolved = await locations.ResolveByMseObfuscatedHashAsync(normalized, cancellationToken);
        return BuildContext(resolved, resolved?.Torrent.InfoHashHex?.ToLowerInvariant());
    }

    static PeerWireTorrentContext? BuildContext(
        TorrentServingLocation? resolved,
        string? expectedInfoHashHex)
    {
        var torrent = resolved?.Torrent;
        if (torrent is null || torrent.PieceLength is null || torrent.PieceCount is null || torrent.TorrentBytes is null)
            return null;

        if (!PeerWireBencode.TryExtractTorrentInfo(torrent.TorrentBytes, out var infoBytes))
            return null;

        if (!string.Equals(InfoHashUtil.Sha1Hex(infoBytes), expectedInfoHashHex, StringComparison.Ordinal))
            return null;

        return new PeerWireTorrentContext(
            torrent.InfoHashHex!,
            resolved!.Source,
            resolved.Metadata.Path,
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
