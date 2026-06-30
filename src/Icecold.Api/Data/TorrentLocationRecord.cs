namespace Icecold.Api.Data;

public sealed class TorrentLocationRecord
{
    public Guid Id { get; set; }

    public Guid TorrentId { get; set; }

    public TorrentRecord? Torrent { get; set; }

    public string SourceName { get; set; } = "";

    public string SourcePath { get; set; } = "";

    public long ContentLength { get; set; }

    public string? ContentVersion { get; set; }

    public DateTimeOffset? ContentLastModified { get; set; }

    public TorrentLocationStatus Status { get; set; }

    public bool IsPrimary { get; set; }

    public int Priority { get; set; }

    public DateTimeOffset? LastVerifiedAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
