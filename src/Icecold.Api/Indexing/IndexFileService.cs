using System.Data;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Indexing;

public sealed class IndexFileService(
    ContentSourceRegistry sources,
    IcecoldDbContext db,
    IIndexingQueue queue)
{
    public async Task<IndexFileSubmission> SubmitFileAsync(IndexFileCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Source) || string.IsNullOrWhiteSpace(command.Path))
            return IndexFileSubmission.BadRequest("source and path are required");

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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;

            try
            {
                var existingCandidates = await db.Torrents
                    .Where(t =>
                        t.SourceName == metadata.SourceName
                        && t.SourcePath == metadata.Path
                        && t.ContentLength == metadata.Length
                        && t.ContentVersion == metadata.Version)
                    .ToListAsync(cancellationToken);

                var existing = existingCandidates
                    .OrderBy(TorrentRecordReuse.Priority)
                    .ThenByDescending(t => t.UpdatedAt)
                    .FirstOrDefault();

                if (existing is not null)
                {
                    if (existing.Status == TorrentStatus.Failed)
                    {
                        ResetFailedRecord(existing, now);
                        await db.SaveChangesAsync(cancellationToken);
                        if (transaction is not null)
                            await transaction.CommitAsync(cancellationToken);

                        await queue.EnqueueAsync(existing.Id, CancellationToken.None);
                        return IndexFileSubmission.Accepted(existing);
                    }

                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);

                    return existing.Status is TorrentStatus.Ready or TorrentStatus.Duplicate
                        ? IndexFileSubmission.Completed(existing)
                        : IndexFileSubmission.Accepted(existing);
                }

                var torrent = new TorrentRecord
                {
                    Id = Guid.NewGuid(),
                    SourceName = metadata.SourceName,
                    SourcePath = metadata.Path,
                    DisplayName = NormalizeDisplayName(command.DisplayName, metadata.DisplayName),
                    ContentLength = metadata.Length,
                    ContentVersion = metadata.Version,
                    ContentLastModified = metadata.LastModified,
                    Status = TorrentStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                db.Torrents.Add(torrent);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                await queue.EnqueueAsync(torrent.Id, CancellationToken.None);
                return IndexFileSubmission.Accepted(torrent);
            }
            catch (Exception ex) when (DatabaseRetry.IsSerializationFailure(ex) && attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }

        return IndexFileSubmission.Conflict("Concurrent index request could not be serialized; retry the request.");
    }

    static string NormalizeDisplayName(string? requestedDisplayName, string metadataDisplayName)
        => string.IsNullOrWhiteSpace(requestedDisplayName) ? metadataDisplayName : requestedDisplayName;

    static void ResetFailedRecord(TorrentRecord torrent, DateTimeOffset now)
    {
        torrent.Status = TorrentStatus.Pending;
        torrent.Error = null;
        torrent.InfoHashHex = null;
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
