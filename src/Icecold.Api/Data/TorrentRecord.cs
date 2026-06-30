namespace Icecold.Api.Data;

public sealed class TorrentRecord
{
    public Guid Id { get; set; }

    public string SourceName { get; set; } = "";

    public string SourcePath { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public long ContentLength { get; set; }

    public string? ContentVersion { get; set; }

    public DateTimeOffset? ContentLastModified { get; set; }

    public TorrentStatus Status { get; set; }

    public string? InfoHashHex { get; set; }

    public string? MseObfuscatedHashHex { get; set; }

    public Guid? DuplicateOfId { get; set; }

    public byte[]? TorrentBytes { get; set; }

    public int? PieceLength { get; set; }

    public int? PieceCount { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
