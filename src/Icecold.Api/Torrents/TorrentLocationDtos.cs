using System.ComponentModel.DataAnnotations;
using Icecold.Api.Content;
using Icecold.Api.Data;

namespace Icecold.Api.Torrents;

public sealed record TorrentLocationResponse(
    Guid Id,
    Guid TorrentId,
    string Source,
    string Path,
    long ContentLength,
    string? ContentVersion,
    DateTimeOffset? ContentLastModified,
    string Status,
    bool IsPrimary,
    int Priority,
    DateTimeOffset? LastVerifiedAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TorrentLocationResponse From(TorrentLocationRecord location)
        => new(
            location.Id,
            location.TorrentId,
            location.SourceName,
            location.SourcePath,
            location.ContentLength,
            location.ContentVersion,
            location.ContentLastModified,
            location.Status.ToString(),
            location.IsPrimary,
            location.Priority,
            location.LastVerifiedAt,
            location.LastError,
            location.CreatedAt,
            location.UpdatedAt);
}

public sealed record AddTorrentLocationRequest(
    [property: Required, MaxLength(128)] string Source,
    [property: Required, MaxLength(4096)] string Path,
    bool MakePrimary = false,
    int? Priority = null);

public sealed record TorrentLocationOperationResult(
    TorrentLocationOperationStatus Status,
    TorrentLocationResponse? Location,
    string? Error);

public enum TorrentLocationOperationStatus
{
    Success,
    NotFound,
    BadRequest,
    Conflict
}

public sealed record TorrentServingLocation(
    TorrentRecord Torrent,
    TorrentLocationRecord Location,
    IContentSource Source,
    ContentMetadata Metadata);
