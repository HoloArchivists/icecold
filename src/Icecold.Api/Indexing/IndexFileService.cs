using Icecold.Api.Content;
using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Indexing;

public sealed class IndexFileService(
    ContentSourceRegistry sources,
    IcecoldDbContext db,
    IIndexingQueue queue)
{
    const int MaxSubmitAttempts = 8;
    const int MaxDisplayNameLength = 512;
    const int MaxSourcePathLength = 4096;

    public async Task<IndexFileSubmission> SubmitFileAsync(IndexFileCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Source) || string.IsNullOrWhiteSpace(command.Path))
            return IndexFileSubmission.BadRequest("source and path are required");

        if (command.Path.Length > MaxSourcePathLength)
            return IndexFileSubmission.BadRequest($"path must be at most {MaxSourcePathLength} characters");

        IContentSource source;
        try
        {
            source = sources.GetRequired(command.Source);
        }
        catch (ContentSourceNotFoundException ex)
        {
            return IndexFileSubmission.NotFound(ex.Message);
        }

        ContentMetadata metadata;
        try
        {
            metadata = await source.GetMetadataAsync(command.Path, cancellationToken);
        }
        catch (ContentItemNotFoundException ex)
        {
            return IndexFileSubmission.NotFound(ex.Message);
        }
        catch (ContentSourceException ex)
        {
            return IndexFileSubmission.BadRequest(ex.Message);
        }

        if (metadata.Length == 0)
            return IndexFileSubmission.BadRequest("zero-length files cannot be indexed");

        if (metadata.Path.Length > MaxSourcePathLength)
            return IndexFileSubmission.BadRequest($"path must be at most {MaxSourcePathLength} characters");

        var displayName = NormalizeDisplayName(command.DisplayName, metadata.DisplayName);
        if (displayName.Length > MaxDisplayNameLength)
            return IndexFileSubmission.BadRequest($"displayName must be at most {MaxDisplayNameLength} characters");

        for (var attempt = 0; attempt < MaxSubmitAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                var existing = await FindReusableRecordAsync(metadata, cancellationToken);

                if (existing is not null)
                {
                    if (existing.Status == TorrentStatus.Failed)
                    {
                        ResetFailedRecord(existing, now);
                        await db.SaveChangesAsync(cancellationToken);

                        // A full queue is fine: the row is persisted as Pending and the worker's sweep picks it up.
                        queue.TryEnqueue(existing.Id);
                        return IndexFileSubmission.Accepted(existing);
                    }

                    return existing.Status is TorrentStatus.Ready or TorrentStatus.Duplicate
                        ? IndexFileSubmission.Completed(existing)
                        : IndexFileSubmission.Accepted(existing);
                }

                var torrent = new TorrentRecord
                {
                    Id = Guid.NewGuid(),
                    SourceName = metadata.SourceName,
                    SourcePath = metadata.Path,
                    DisplayName = displayName,
                    ContentLength = metadata.Length,
                    ContentVersion = metadata.Version,
                    ContentLastModified = metadata.LastModified,
                    Status = TorrentStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                db.Torrents.Add(torrent);
                await db.SaveChangesAsync(cancellationToken);

                queue.TryEnqueue(torrent.Id);
                return IndexFileSubmission.Accepted(torrent);
            }
            catch (Exception ex) when (DatabaseRetry.IsSerializationOrUniqueConflict(ex))
            {
                db.ChangeTracker.Clear();
                if (attempt == MaxSubmitAttempts - 1)
                    break;

                await DelayForRetryAsync(attempt, cancellationToken);
            }
        }

        return IndexFileSubmission.Conflict("Concurrent index request could not be serialized; retry the request.");
    }

    async Task<TorrentRecord?> FindReusableRecordAsync(ContentMetadata metadata, CancellationToken cancellationToken)
    {
        var existingCandidates = await db.Torrents
            .Include(t => t.Locations)
            .Where(t =>
                t.SourceName == metadata.SourceName
                && t.SourcePath == metadata.Path
                && t.ContentLength == metadata.Length
                && t.ContentVersion == metadata.Version)
            .ToListAsync(cancellationToken);

        return existingCandidates
            .OrderBy(TorrentRecordReuse.Priority)
            .ThenByDescending(t => t.UpdatedAt)
            .FirstOrDefault();
    }

    static string NormalizeDisplayName(string? requestedDisplayName, string metadataDisplayName)
        => string.IsNullOrWhiteSpace(requestedDisplayName) ? metadataDisplayName : requestedDisplayName;

    static Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, 10 * (attempt + 1))), cancellationToken);

    static void ResetFailedRecord(TorrentRecord torrent, DateTimeOffset now)
    {
        torrent.Status = TorrentStatus.Pending;
        torrent.Error = null;
        torrent.Attempts = 0;
        torrent.InfoHashHex = null;
        torrent.MseObfuscatedHashHex = null;
        torrent.DuplicateOfId = null;
        torrent.TorrentBytes = null;
        torrent.PieceLength = null;
        torrent.PieceCount = null;
        torrent.CompletedAt = null;
        torrent.UpdatedAt = now;
    }
}

public sealed record IndexFileCommand(string Source, string Path, string? DisplayName);

public sealed record IndexFileSubmission(IndexFileSubmissionStatus Status, TorrentRecord? Torrent, string? Error)
{
    public static IndexFileSubmission Accepted(TorrentRecord torrent)
        => new(IndexFileSubmissionStatus.Accepted, torrent, null);

    public static IndexFileSubmission Completed(TorrentRecord torrent)
        => new(IndexFileSubmissionStatus.Completed, torrent, null);

    public static IndexFileSubmission NotFound(string error)
        => new(IndexFileSubmissionStatus.NotFound, null, error);

    public static IndexFileSubmission BadRequest(string error)
        => new(IndexFileSubmissionStatus.BadRequest, null, error);

    public static IndexFileSubmission Conflict(string error)
        => new(IndexFileSubmissionStatus.Conflict, null, error);
}

public enum IndexFileSubmissionStatus
{
    Accepted,
    Completed,
    NotFound,
    BadRequest,
    Conflict
}

static class TorrentRecordReuse
{
    public static int Priority(TorrentRecord torrent)
        => torrent.Status switch
        {
            TorrentStatus.Ready => 0,
            TorrentStatus.Duplicate => 1,
            TorrentStatus.Hashing => 2,
            TorrentStatus.Pending => 3,
            TorrentStatus.Failed => 4,
            _ => 4
        };
}
