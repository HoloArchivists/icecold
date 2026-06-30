using Icecold.Api.Data;

namespace Icecold.Api.Torrents;

public sealed record IndexResponse(
    Guid Id,
    string Source,
    string Path,
    string DisplayName,
    long ContentLength,
    string Status,
    string? InfoHash,
    Guid? DuplicateOfId,
    int? PieceLength,
    int? PieceCount,
    string? Error,
    IReadOnlyList<TorrentLocationResponse> Locations)
{
    public static IndexResponse From(TorrentRecord torrent)
        => new(
            torrent.Id,
            torrent.SourceName,
            torrent.SourcePath,
            torrent.DisplayName,
            torrent.ContentLength,
            torrent.Status.ToString(),
            torrent.InfoHashHex,
            torrent.DuplicateOfId,
            torrent.PieceLength,
            torrent.PieceCount,
            torrent.Error,
            torrent.Locations
                .OrderByDescending(l => l.IsPrimary)
                .ThenBy(l => l.Priority)
                .ThenBy(l => l.CreatedAt)
                .Select(TorrentLocationResponse.From)
                .ToList());
}
