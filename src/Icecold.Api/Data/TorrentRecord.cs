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

    // Higher runs first. Callers may raise it while the job is queued.
    public int Priority { get; set; }

    // Queue order within a priority, and the earliest time a retried job may be claimed again.
    public DateTimeOffset AvailableAt { get; set; }

    // Identifies the claim currently hashing this row. Every write after the claim is fenced on it,
    // so a worker whose lease expired cannot overwrite the result of the worker that took over.
    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    // Hashing progress, persisted with each lease renewal.
    public long HashedBytes { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<TorrentLocationRecord> Locations { get; set; } = [];
}
