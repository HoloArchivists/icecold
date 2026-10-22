using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Indexing;

public sealed class IndexingWorker(
    IndexingSignal signal,
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

    static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromMinutes(10);

    readonly string instanceId = $"{Environment.MachineName}/{Environment.ProcessId}";

    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(options.Value.Indexing.PollIntervalSeconds);

    internal TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(options.Value.Indexing.LeaseDurationSeconds);

    // Delay before the first retry of a job that hit a retryable failure; doubles per attempt.
    internal TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(15);

    // Renewals also persist hashing progress, so they run more often than lease expiry requires.
    TimeSpan HeartbeatInterval => TimeSpan.FromTicks(Math.Min(LeaseDuration.Ticks / 3, TimeSpan.FromSeconds(5).Ticks));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, options.Value.Indexing.MaxConcurrency);
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunWorkerAsync(stoppingToken)));
    }

    async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Unique per claim, so two claims of the same row are told apart even within a process.
            var leaseOwner = $"{instanceId}/{Guid.NewGuid():n}";
            TorrentRecord? torrent;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                torrent = await scope.ServiceProvider.GetRequiredService<IndexingQueue>()
                    .ClaimNextAsync(leaseOwner, LeaseDuration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Claiming an indexing job failed; retrying after the poll interval.");
                torrent = null;
            }

            if (torrent is null)
            {
                try
                {
                    await signal.WaitAsync(PollInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                await ProcessAsync(torrent, leaseOwner, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Typically the database stayed unreachable through the status-update retries.
                // The lease then expires and the job is reclaimed.
                logger.LogError(ex, "Unhandled indexing failure for torrent {TorrentId}", torrent.Id);
            }
        }
    }

    async Task ProcessAsync(TorrentRecord torrent, string leaseOwner, CancellationToken cancellationToken)
    {
        var torrentId = torrent.Id;
        if (torrent.Attempts > MaxIndexingAttempts)
        {
            // Earlier claims never finished (their leases expired), e.g. the process kept crashing on this file.
            await UpdateLeasedWithRetryAsync(
                queue => queue.FailAsync(torrentId, leaseOwner, $"Gave up after {MaxIndexingAttempts} attempts that did not finish.", cancellationToken),
                cancellationToken);
            return;
        }

        try
        {
            TorrentBuildResult result;
            await using (var lease = new IndexingLease(this, torrentId, leaseOwner, cancellationToken))
            {
                try
                {
                    var source = sources.GetRequired(torrent.SourceName);
                    var metadata = await source.GetMetadataAsync(torrent.SourcePath, lease.Token);
                    if (metadata.Length != torrent.ContentLength || metadata.Version != torrent.ContentVersion)
                        throw new ContentSourceException("Content metadata changed since the indexing request was accepted.");

                    result = await torrentBuilder.BuildSingleFileAsync(
                        metadata,
                        source,
                        torrent.DisplayName,
                        lease.Token,
                        lease.ReportProgress);
                }
                catch (OperationCanceledException) when (lease.Lost && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Lost the lease on torrent {TorrentId}; another worker owns it now.", torrentId);
                    return;
                }
            }

            if (!await FinalizeAsync(torrentId, leaseOwner, result, cancellationToken))
                await RequeueForRetryAsync(torrent, leaseOwner, "Torrent finalization hit repeated database conflicts.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: hand the job back now rather than leaving it until the lease expires.
            await ReleaseOnShutdownAsync(torrentId, leaseOwner);
            throw;
        }
        catch (ContentSourceUnavailableException ex)
        {
            logger.LogWarning(ex, "Backing store unavailable while indexing torrent {TorrentId}; requeueing.", torrentId);
            await RequeueForRetryAsync(torrent, leaseOwner, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is ContentSourceException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Indexing failed for torrent {TorrentId}", torrentId);
            await UpdateLeasedWithRetryAsync(queue => queue.FailAsync(torrentId, leaseOwner, ex.Message, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (DatabaseRetry.IsSerializationOrUniqueConflict(ex))
        {
            logger.LogWarning(ex, "Indexing database conflict for torrent {TorrentId}; requeueing.", torrentId);
            await RequeueForRetryAsync(torrent, leaseOwner, "Indexing hit a transient database conflict.", cancellationToken);
        }
        catch (Exception ex) when (DatabaseRetry.IsTransient(ex))
        {
            logger.LogWarning(ex, "Transient database error while indexing torrent {TorrentId}; requeueing.", torrentId);
            await RequeueForRetryAsync(torrent, leaseOwner, "Indexing hit a transient database error.", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed unexpectedly for torrent {TorrentId}", torrentId);
            await UpdateLeasedWithRetryAsync(queue => queue.FailAsync(torrentId, leaseOwner, ex.Message, cancellationToken), cancellationToken);
        }
    }

    async Task<bool> FinalizeAsync(Guid torrentId, string leaseOwner, TorrentBuildResult result, CancellationToken cancellationToken)
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
                // The row lock keeps the lease from being reclaimed between this check and the commit.
                var torrent = db.Database.IsRelational()
                    ? (await db.Torrents.FromSql($"""SELECT * FROM torrents WHERE "Id" = {torrentId} FOR UPDATE""")
                        .ToListAsync(cancellationToken)).FirstOrDefault()
                    : await db.Torrents.FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
                if (torrent is null || torrent.Status != TorrentStatus.Hashing || torrent.LeaseOwner != leaseOwner)
                {
                    logger.LogWarning("Discarding the hash of torrent {TorrentId}; its lease is no longer held.", torrentId);
                    return true;
                }

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
                torrent.LeaseOwner = null;
                torrent.LeaseExpiresAt = null;
                torrent.HashedBytes = torrent.ContentLength;
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

    // Retryable failures back off exponentially per attempt so a flapping origin or database is
    // not hammered with full re-hashes.
    async Task RequeueForRetryAsync(TorrentRecord torrent, string leaseOwner, string reason, CancellationToken cancellationToken)
    {
        var torrentId = torrent.Id;
        if (torrent.Attempts >= MaxIndexingAttempts)
        {
            await UpdateLeasedWithRetryAsync(
                queue => queue.FailAsync(torrentId, leaseOwner, $"{reason} Gave up after {torrent.Attempts} attempts.", cancellationToken),
                cancellationToken);
            return;
        }

        var backoff = TimeSpan.FromTicks(Math.Min(
            MaxRetryBackoff.Ticks,
            RetryBackoff.Ticks * (1L << Math.Clamp(torrent.Attempts - 1, 0, 20))));
        await UpdateLeasedWithRetryAsync(
            queue => queue.RequeueAsync(torrentId, leaseOwner, DateTimeOffset.UtcNow + backoff, reason, cancellationToken),
            cancellationToken);
    }

    async Task ReleaseOnShutdownAsync(Guid torrentId, string leaseOwner)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IndexingQueue>().ReleaseAsync(torrentId, leaseOwner, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release torrent {TorrentId} on shutdown; it is reclaimed when its lease expires.", torrentId);
        }
    }

    async Task UpdateLeasedWithRetryAsync(
        Func<IndexingQueue, Task<bool>> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxFinalizeAttempts; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await update(scope.ServiceProvider.GetRequiredService<IndexingQueue>());
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

    async Task<bool> RenewLeaseAsync(Guid torrentId, string leaseOwner, long hashedBytes, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IndexingQueue>()
            .RenewLeaseAsync(torrentId, leaseOwner, LeaseDuration, hashedBytes, cancellationToken);
    }

    void LogRenewFailure(Exception exception, Guid torrentId)
        => logger.LogWarning(exception, "Renewing the lease on torrent {TorrentId} failed.", torrentId);

    // Renews the lease (and records progress) in the background while a job hashes. If a renewal
    // finds the lease gone, Token is cancelled so the worker stops hashing a job it no longer owns.
    sealed class IndexingLease : IAsyncDisposable
    {
        readonly IndexingWorker worker;
        readonly Guid torrentId;
        readonly string leaseOwner;
        readonly CancellationTokenSource job;
        readonly CancellationTokenSource stopping = new();
        readonly Task heartbeat;
        long hashedBytes;

        public IndexingLease(IndexingWorker worker, Guid torrentId, string leaseOwner, CancellationToken cancellationToken)
        {
            this.worker = worker;
            this.torrentId = torrentId;
            this.leaseOwner = leaseOwner;
            job = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeat = RunAsync();
        }

        public CancellationToken Token => job.Token;

        public bool Lost { get; private set; }

        public void ReportProgress(long bytes) => Interlocked.Exchange(ref hashedBytes, bytes);

        async Task RunAsync()
        {
            using var timer = new PeriodicTimer(worker.HeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stopping.Token))
                {
                    try
                    {
                        if (!await worker.RenewLeaseAsync(torrentId, leaseOwner, Interlocked.Read(ref hashedBytes), stopping.Token))
                        {
                            Lost = true;
                            await job.CancelAsync();
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Keep hashing; if the database stays away long enough the lease expires
                        // and the next successful renewal reports it lost.
                        worker.LogRenewFailure(ex, torrentId);
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            await heartbeat;
            stopping.Dispose();
            job.Dispose();
        }
    }
}
