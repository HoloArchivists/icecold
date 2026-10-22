using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Indexing;

public sealed class IndexFileService(
    ContentSourceRegistry sources,
    IcecoldDbContext db,
    IndexingSignal signal)
{
    const int MaxSubmitAttempts = 8;
    const int MaxDisplayNameLength = 512;
    const int MaxSourcePathLength = 4096;
    const int MaxConcurrentStats = 8;

    internal const int MaxFilesPerTorrent = 1000;

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

        return await SubmitAsync(
            metadata.SourceName,
            metadata.Path,
            metadata.Length,
            metadata.Version,
            command.Priority,
            () => new TorrentRecord
            {
                Id = Guid.NewGuid(),
                SourceName = metadata.SourceName,
                SourcePath = metadata.Path,
                DisplayName = displayName,
                ContentLength = metadata.Length,
                ContentVersion = metadata.Version,
                ContentLastModified = metadata.LastModified
            },
            cancellationToken);
    }

    public async Task<IndexFileSubmission> SubmitFilesAsync(IndexFilesCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Source))
            return IndexFileSubmission.BadRequest("source is required");

        if (command.Name is null)
            return IndexFileSubmission.BadRequest("name is required");

        if (MultiFileManifest.ValidateName(command.Name) is { } nameError)
            return IndexFileSubmission.BadRequest(nameError);

        if (command.Name.Length > MaxDisplayNameLength)
            return IndexFileSubmission.BadRequest($"name must be at most {MaxDisplayNameLength} characters");

        if (command.Files is not { Count: > 0 })
            return IndexFileSubmission.BadRequest("files must list at least one file");

        if (command.Files.Count > MaxFilesPerTorrent)
            return IndexFileSubmission.BadRequest($"files must list at most {MaxFilesPerTorrent} files");

        foreach (var file in command.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                return IndexFileSubmission.BadRequest("every file needs a path");

            if (file.Path.Length > MaxSourcePathLength)
                return IndexFileSubmission.BadRequest($"path must be at most {MaxSourcePathLength} characters");

            if (file.TorrentPath is not null && file.TorrentPath.Length > MaxSourcePathLength)
                return IndexFileSubmission.BadRequest($"torrentPath must be at most {MaxSourcePathLength} characters");
        }

        IContentSource source;
        try
        {
            source = sources.GetRequired(command.Source);
        }
        catch (ContentSourceNotFoundException ex)
        {
            return IndexFileSubmission.NotFound(ex.Message);
        }

        ContentMetadata[] metadata;
        try
        {
            metadata = await StatFilesAsync(source, command.Files, cancellationToken);
        }
        catch (ContentItemNotFoundException ex)
        {
            return IndexFileSubmission.NotFound(ex.Message);
        }
        catch (ContentSourceException ex)
        {
            return IndexFileSubmission.BadRequest(ex.Message);
        }

        var entries = new List<MultiFileTorrentEntry>(metadata.Length);
        for (var i = 0; i < metadata.Length; i++)
        {
            if (metadata[i].Length == 0)
                return IndexFileSubmission.BadRequest($"zero-length file '{metadata[i].Path}' cannot be indexed");

            if (metadata[i].Path.Length > MaxSourcePathLength)
                return IndexFileSubmission.BadRequest($"path must be at most {MaxSourcePathLength} characters");

            var torrentPath = command.Files[i].TorrentPath ?? metadata[i].DisplayName;
            if (MultiFileManifest.ValidateTorrentPath(torrentPath) is { } pathError)
                return IndexFileSubmission.BadRequest(pathError);

            entries.Add(new MultiFileTorrentEntry(metadata[i], torrentPath));
        }

        if (MultiFileManifest.FindConflict(entries.Select(e => e.TorrentPath).ToList()) is { } conflict)
            return IndexFileSubmission.BadRequest(conflict);

        var manifestHash = MultiFileManifest.Hash(command.Name, entries);
        var sourcePath = MultiFileManifest.SourcePathPrefix + manifestHash;
        var contentLength = entries.Sum(e => e.Metadata.Length);
        return await SubmitAsync(
            source.Name,
            sourcePath,
            contentLength,
            manifestHash,
            command.Priority,
            () =>
            {
                var id = Guid.NewGuid();
                return new TorrentRecord
                {
                    Id = id,
                    SourceName = source.Name,
                    SourcePath = sourcePath,
                    DisplayName = command.Name,
                    ContentLength = contentLength,
                    ContentVersion = manifestHash,
                    ContentLastModified = entries.Max(e => e.Metadata.LastModified),
                    IsMultiFile = true,
                    Files = entries
                        .Select((e, index) => new TorrentFileRecord
                        {
                            Id = Guid.NewGuid(),
                            TorrentId = id,
                            Index = index,
                            SourcePath = e.Metadata.Path,
                            TorrentPath = e.TorrentPath,
                            ContentLength = e.Metadata.Length,
                            ContentVersion = e.Metadata.Version,
                            ContentLastModified = e.Metadata.LastModified
                        })
                        .ToList()
                };
            },
            cancellationToken);
    }

    static async Task<ContentMetadata[]> StatFilesAsync(
        IContentSource source,
        IReadOnlyList<IndexFilesEntry> files,
        CancellationToken cancellationToken)
    {
        var metadata = new ContentMetadata[files.Count];
        await Parallel.ForAsync(
            0,
            files.Count,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStats, CancellationToken = cancellationToken },
            async (i, ct) => metadata[i] = await source.GetMetadataAsync(files[i].Path, ct));
        return metadata;
    }

    // Enqueues a new record for the content identity, or returns the record already holding it.
    async Task<IndexFileSubmission> SubmitAsync(
        string sourceName,
        string sourcePath,
        long contentLength,
        string? contentVersion,
        int? priority,
        Func<TorrentRecord> create,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxSubmitAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                var existing = await FindReusableRecordAsync(sourceName, sourcePath, contentLength, contentVersion, cancellationToken);

                if (existing is not null)
                {
                    if (existing.Status == TorrentStatus.Failed)
                    {
                        ResetFailedRecord(existing, priority, now);
                        await db.SaveChangesAsync(cancellationToken);
                        signal.Notify();
                        return IndexFileSubmission.Accepted(existing);
                    }

                    if (existing.Status is TorrentStatus.Ready or TorrentStatus.Duplicate)
                        return IndexFileSubmission.Completed(existing);

                    // Resubmitting a queued job can only raise its priority, so repeated requests
                    // from a caller that sees demand never demote a job someone else bumped.
                    if (priority > existing.Priority)
                    {
                        existing.Priority = priority.Value;
                        existing.UpdatedAt = now;
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    return IndexFileSubmission.Accepted(existing);
                }

                var torrent = create();
                torrent.Status = TorrentStatus.Pending;
                torrent.Priority = priority ?? 0;
                torrent.AvailableAt = now;
                torrent.CreatedAt = now;
                torrent.UpdatedAt = now;

                db.Torrents.Add(torrent);
                await db.SaveChangesAsync(cancellationToken);

                signal.Notify();
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

    async Task<TorrentRecord?> FindReusableRecordAsync(
        string sourceName,
        string sourcePath,
        long contentLength,
        string? contentVersion,
        CancellationToken cancellationToken)
    {
        var existingCandidates = await db.Torrents
            .Include(t => t.Locations)
            .Include(t => t.Files)
            .Where(t =>
                t.SourceName == sourceName
                && t.SourcePath == sourcePath
                && t.ContentLength == contentLength
                && t.ContentVersion == contentVersion)
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

    static void ResetFailedRecord(TorrentRecord torrent, int? priority, DateTimeOffset now)
    {
        torrent.Status = TorrentStatus.Pending;
        torrent.Priority = priority ?? torrent.Priority;
        torrent.AvailableAt = now;
        IndexingQueue.ClearLease(torrent);
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

public sealed record IndexFileCommand(string Source, string Path, string? DisplayName, int? Priority = null);

public sealed record IndexFilesCommand(string Source, string? Name, IReadOnlyList<IndexFilesEntry>? Files, int? Priority = null);

// TorrentPath defaults to the file's name.
public sealed record IndexFilesEntry(string Path, string? TorrentPath = null);

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
