using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Indexing;

public sealed class IndexingWorker(
    IIndexingQueue queue,
    IServiceScopeFactory scopeFactory,
    ContentSourceRegistry sources,
    TorrentBuilder torrentBuilder,
    IOptions<IcecoldOptions> options,
    ILogger<IndexingWorker> logger) : BackgroundService
{
    const int MaxFinalizeAttempts = 16;

    // Claims per job (Attempts is incremented on every claim). Retryable failures past this
    // mark the job Failed instead of requeueing it forever; resubmitting resets the counter.
    internal const int MaxIndexingAttempts = 5;

    // The in-memory queue is bounded and never blocks writers, so Pending rows that did not fit
    // (or whose enqueue was skipped) are picked up from the database by this periodic sweep.
    internal TimeSpan PendingSweepInterval { get; init; } = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, options.Value.Indexing.MaxConcurrency);
        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => RunWorkerAsync(stoppingToken))
            .ToList();
        workers.Add(SweepPendingAsync(stoppingToken));

        await Task.WhenAll(workers);
    }

    async Task SweepPendingAsync(CancellationToken cancellationToken)
    {
        var interruptedReset = false;
        using var timer = new PeriodicTimer(PendingSweepInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var claims = scope.ServiceProvider.GetRequiredService<IndexingClaimService>();
                if (!interruptedReset)
                {
                    await claims.ResetInterruptedAsync(cancellationToken);
                    interruptedReset = true;
                }

                var capacity = Math.Max(1, options.Value.Indexing.QueueCapacity);
                foreach (var id in await claims.ListPendingAsync(capacity, cancellationToken))
                {
                    if (!queue.TryEnqueue(id))
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pending indexing sweep failed; retrying on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var torrentId in queue.DequeueAllAsync(cancellationToken))
        {
            try
            {
                await ProcessAsync(torrentId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Typically the database stayed unreachable through the status-update retries.
                // The row may be left Hashing until the next process start resets it.
                logger.LogError(ex, "Unhandled indexing failure for torrent {TorrentId}", torrentId);
            }
        }
    }

    async Task ProcessAsync(Guid torrentId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<IndexingClaimService>();
        var torrent = await claims.ClaimForHashingAsync(torrentId, cancellationToken);
        if (torrent is null)
            return;

        try
        {
            var source = sources.GetRequired(torrent.SourceName);
            var metadata = await source.GetMetadataAsync(torrent.SourcePath, cancellationToken);
            if (metadata.Length != torrent.ContentLength || metadata.Version != torrent.ContentVersion)
                throw new ContentSourceException("Content metadata changed since the indexing request was accepted.");

            var result = await torrentBuilder.BuildSingleFileAsync(metadata, source, torrent.DisplayName, cancellationToken);

            if (!await FinalizeAsync(torrent.Id, result, cancellationToken))
            {
                await RequeueForRetryAsync(
                    torrent.Id,
                    "Torrent finalization hit repeated database conflicts.",
                    cancellationToken);
            }

            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: leave the row Hashing; the next start resets it to Pending.
            throw;
        }
        catch (Exception ex) when (ex is ContentSourceException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Indexing failed for torrent {TorrentId}", torrentId);
            await MarkFailedAsync(torrentId, ex.Message, cancellationToken);
            return;
        }
        catch (Exception ex) when (DatabaseRetry.IsSerializationOrUniqueConflict(ex))
        {
            logger.LogWarning(ex, "Indexing database conflict for torrent {TorrentId}; requeueing.", torrentId);
            await RequeueForRetryAsync(torrentId, "Indexing hit a transient database conflict.", cancellationToken);
            return;
        }
        catch (Exception ex) when (DatabaseRetry.IsTransient(ex))
        {
            logger.LogWarning(ex, "Transient database error while indexing torrent {TorrentId}; requeueing.", torrentId);
            await RequeueForRetryAsync(torrentId, "Indexing hit a transient database error.", cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed unexpectedly for torrent {TorrentId}", torrentId);
            await MarkFailedAsync(torrentId, ex.Message, cancellationToken);
            return;
        }
    }

    async Task<bool> FinalizeAsync(Guid torrentId, TorrentBuildResult result, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();

        for (var attempt = 0; attempt < MaxFinalizeAttempts; attempt++)
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            try
            {
                var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
                if (torrent is null || torrent.Status is TorrentStatus.Ready or TorrentStatus.Duplicate)
                    return true;

                var canonical = await db.Torrents
                    .Where(t => t.Id != torrentId && t.InfoHashHex == result.InfoHashHex && t.Status == TorrentStatus.Ready)
                    .OrderBy(t => t.CreatedAt)
                    .ThenBy(t => t.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                var completedAt = DateTimeOffset.UtcNow;
                torrent.InfoHashHex = result.InfoHashHex;
                torrent.MseObfuscatedHashHex = PeerWireMse.HashReq2Hex(result.InfoHashHex);
                torrent.PieceLength = result.PieceLength;
                torrent.PieceCount = result.PieceCount;
                torrent.Error = null;
                torrent.CompletedAt = completedAt;
                torrent.UpdatedAt = completedAt;

                if (canonical is null)
                {
                    torrent.TorrentBytes = result.TorrentBytes;
                    torrent.DuplicateOfId = null;
                    torrent.Status = TorrentStatus.Ready;
                    await UpsertIndexedLocationAsync(db, torrent, torrent.Id, makePrimary: true, completedAt, cancellationToken);
                }
                else
                {
                    torrent.TorrentBytes = null;
                    torrent.DuplicateOfId = canonical.Id;
                    torrent.Status = TorrentStatus.Duplicate;
                    await UpsertIndexedLocationAsync(db, torrent, canonical.Id, makePrimary: false, completedAt, cancellationToken);
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                return true;
            }
            catch (Exception ex) when (
                (DatabaseRetry.IsSerializationOrUniqueConflict(ex) || DatabaseRetry.IsTransient(ex))
                && attempt < MaxFinalizeAttempts - 1)
            {
                // Retry finalization in place so a database blip does not force a full rehash.
                db.ChangeTracker.Clear();
                await DelayForRetryAsync(attempt, cancellationToken);
            }
        }

        return false;
    }

    static async Task UpsertIndexedLocationAsync(
        IcecoldDbContext db,
        TorrentRecord indexedTorrent,
        Guid canonicalTorrentId,
        bool makePrimary,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var location = await db.TorrentLocations.FirstOrDefaultAsync(
            l => l.TorrentId == canonicalTorrentId
                && l.SourceName == indexedTorrent.SourceName
                && l.SourcePath == indexedTorrent.SourcePath
                && l.ContentLength == indexedTorrent.ContentLength
                && l.ContentVersion == indexedTorrent.ContentVersion,
            cancellationToken);

        if (location is null)
        {
            location = new TorrentLocationRecord
            {
                Id = Guid.NewGuid(),
                TorrentId = canonicalTorrentId,
                SourceName = indexedTorrent.SourceName,
                SourcePath = indexedTorrent.SourcePath,
                ContentLength = indexedTorrent.ContentLength,
                ContentVersion = indexedTorrent.ContentVersion,
                ContentLastModified = indexedTorrent.ContentLastModified,
                Status = TorrentLocationStatus.Active,
                IsPrimary = false,
                Priority = makePrimary ? 0 : await NextLocationPriorityAsync(db, canonicalTorrentId, cancellationToken),
                LastVerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.TorrentLocations.Add(location);
        }
        else
        {
            location.ContentLastModified = indexedTorrent.ContentLastModified;
            location.Status = TorrentLocationStatus.Active;
            location.LastVerifiedAt = now;
            location.LastError = null;
            location.UpdatedAt = now;
        }

        if (!makePrimary && await HasActivePrimaryAsync(db, canonicalTorrentId, cancellationToken))
            return;

        var locations = await db.TorrentLocations
            .Where(l => l.TorrentId == canonicalTorrentId)
            .ToListAsync(cancellationToken);
        if (!locations.Any(l => l.Id == location.Id))
            locations.Add(location);

        foreach (var current in locations)
            current.IsPrimary = current.Id == location.Id;

        location.Priority = 0;
    }

    static async Task<int> NextLocationPriorityAsync(
        IcecoldDbContext db,
        Guid torrentId,
        CancellationToken cancellationToken)
    {
        var maxPriority = await db.TorrentLocations
            .Where(l => l.TorrentId == torrentId)
            .Select(l => (int?)l.Priority)
            .MaxAsync(cancellationToken);

        return (maxPriority ?? 0) + 10;
    }

    static Task<bool> HasActivePrimaryAsync(
        IcecoldDbContext db,
        Guid torrentId,
        CancellationToken cancellationToken)
        => db.TorrentLocations.AnyAsync(
            l => l.TorrentId == torrentId
                && l.Status == TorrentLocationStatus.Active
                && l.IsPrimary,
            cancellationToken);

    async Task RequeueForRetryAsync(Guid torrentId, string reason, CancellationToken cancellationToken)
    {
        var requeued = false;
        await UpdateStatusWithRetryAsync(
            torrentId,
            torrent =>
            {
                if (torrent.Status is TorrentStatus.Ready or TorrentStatus.Duplicate)
                    return false;

                requeued = torrent.Attempts < MaxIndexingAttempts;
                torrent.Status = requeued ? TorrentStatus.Pending : TorrentStatus.Failed;
                torrent.Error = requeued ? reason : $"{reason} Gave up after {torrent.Attempts} attempts.";
                torrent.UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            },
            cancellationToken);

        // Never wait on the queue from inside a worker: if it is full, the pending sweep re-enqueues.
        if (requeued)
            queue.TryEnqueue(torrentId);
    }

    async Task MarkFailedAsync(Guid torrentId, string error, CancellationToken cancellationToken)
    {
        await UpdateStatusWithRetryAsync(
            torrentId,
            torrent =>
            {
                if (torrent.Status is TorrentStatus.Ready or TorrentStatus.Duplicate)
                    return false;

                torrent.Status = TorrentStatus.Failed;
                torrent.Error = error;
                torrent.UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            },
            cancellationToken);
    }

    async Task UpdateStatusWithRetryAsync(
        Guid torrentId,
        Func<TorrentRecord, bool> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxFinalizeAttempts; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();

            try
            {
                var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
                if (torrent is null || !update(torrent))
                    return;

                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (
                (DatabaseRetry.IsSerializationOrUniqueConflict(ex) || DatabaseRetry.IsTransient(ex))
                && attempt < MaxFinalizeAttempts - 1)
            {
                await DelayForRetryAsync(attempt, cancellationToken);
            }
        }
    }

    static Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 25 * (attempt + 1)));
        return Task.Delay(delay, cancellationToken);
    }
}
