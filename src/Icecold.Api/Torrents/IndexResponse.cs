using Icecold.Api.Data;

namespace Icecold.Api.Torrents;

public sealed record IndexResponse(
    Guid Id,
    string Source,
    string Path,
    string DisplayName,
    long ContentLength,
    string Status,
    int Priority,
    int? QueuePosition,
    long? HashedBytes,
    string? InfoHash,
    Guid? DuplicateOfId,
    int? PieceLength,
    int? PieceCount,
    string? Error,
    IReadOnlyList<TorrentLocationResponse> Locations)
{
    // QueuePosition counts the Pending jobs ahead of this one; HashedBytes is set while Hashing and
    // is persisted every few seconds by the worker that holds the job.
    public static IndexResponse From(TorrentRecord torrent, int? queuePosition = null)
        => new(
            torrent.Id,
            torrent.SourceName,
            torrent.SourcePath,
            torrent.DisplayName,
            torrent.ContentLength,
            torrent.Status.ToString(),
            torrent.Priority,
            torrent.Status == TorrentStatus.Pending ? queuePosition : null,
            torrent.Status == TorrentStatus.Hashing ? torrent.HashedBytes : null,
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
