using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Icecold.Api.Indexing;

// The indexing queue is the torrents table: Pending rows are jobs, ordered by Priority (highest
// first) and then AvailableAt. A worker claims a row by moving it to Hashing under a lease that it
// renews while hashing. If the worker dies, the lease expires and another worker (in this or another
// instance) reclaims the row. Every write after the claim is fenced on LeaseOwner.
public sealed class IndexingQueue(IcecoldDbContext db)
{
    public async Task<TorrentRecord?> ClaimNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now + leaseDuration;
        if (!db.Database.IsRelational())
            return await ClaimNextInMemoryAsync(leaseOwner, now, leaseExpiresAt, cancellationToken);

        // Abandoned claims first: they are rare and were already started. Rows without a lease are
        // Hashing rows from before leases existed.
        var claimed = await db.Torrents.FromSql($"""
            UPDATE torrents AS t
            SET "Status" = 'Hashing', "LeaseOwner" = {leaseOwner}, "LeaseExpiresAt" = {leaseExpiresAt},
                "Attempts" = t."Attempts" + 1, "HashedBytes" = 0, "Error" = NULL, "UpdatedAt" = {now}
            WHERE t."Id" = (
                SELECT c."Id" FROM torrents AS c
                WHERE c."Status" = 'Hashing' AND (c."LeaseExpiresAt" IS NULL OR c."LeaseExpiresAt" < {now})
                ORDER BY c."LeaseExpiresAt" NULLS FIRST
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING t.*
            """).AsNoTracking().ToListAsync(cancellationToken);
        if (claimed.Count > 0)
            return claimed[0];

        claimed = await db.Torrents.FromSql($"""
            UPDATE torrents AS t
            SET "Status" = 'Hashing', "LeaseOwner" = {leaseOwner}, "LeaseExpiresAt" = {leaseExpiresAt},
                "Attempts" = t."Attempts" + 1, "HashedBytes" = 0, "Error" = NULL, "UpdatedAt" = {now}
            WHERE t."Id" = (
                SELECT c."Id" FROM torrents AS c
                WHERE c."Status" = 'Pending' AND c."AvailableAt" <= {now}
                ORDER BY c."Priority" DESC, c."AvailableAt", c."Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING t.*
            """).AsNoTracking().ToListAsync(cancellationToken);
        return claimed.Count > 0 ? claimed[0] : null;
    }

    // Extends the lease and records progress. False means the lease was lost (it expired and the
    // row was reclaimed, or the row was otherwise moved on) and the worker must stop.
    public async Task<bool> RenewLeaseAsync(
        Guid torrentId,
        string leaseOwner,
        TimeSpan leaseDuration,
        long hashedBytes,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await UpdateLeasedAsync(
            torrentId,
            leaseOwner,
            setters => setters
                .SetProperty(t => t.LeaseExpiresAt, now + leaseDuration)
                .SetProperty(t => t.HashedBytes, hashedBytes),
            torrent =>
            {
                torrent.LeaseExpiresAt = now + leaseDuration;
                torrent.HashedBytes = hashedBytes;
            },
            cancellationToken);
    }

    // Returns the job to the queue after a retryable failure; it becomes claimable at availableAt.
    public Task<bool> RequeueAsync(
        Guid torrentId,
        string leaseOwner,
        DateTimeOffset availableAt,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return UpdateLeasedAsync(
            torrentId,
            leaseOwner,
            setters => setters
                .SetProperty(t => t.Status, TorrentStatus.Pending)
                .SetProperty(t => t.AvailableAt, availableAt)
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.LeaseOwner, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(t => t.HashedBytes, 0)
                .SetProperty(t => t.UpdatedAt, now),
            torrent =>
            {
                torrent.Status = TorrentStatus.Pending;
                torrent.AvailableAt = availableAt;
                torrent.Error = error;
                ClearLease(torrent);
                torrent.UpdatedAt = now;
            },
            cancellationToken);
    }

    public Task<bool> FailAsync(
        Guid torrentId,
        string leaseOwner,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return UpdateLeasedAsync(
            torrentId,
            leaseOwner,
            setters => setters
                .SetProperty(t => t.Status, TorrentStatus.Failed)
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.LeaseOwner, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(t => t.HashedBytes, 0)
                .SetProperty(t => t.UpdatedAt, now),
            torrent =>
            {
                torrent.Status = TorrentStatus.Failed;
                torrent.Error = error;
                ClearLease(torrent);
                torrent.UpdatedAt = now;
            },
            cancellationToken);
    }

    // Graceful shutdown: hand the job back immediately, without spending one of its attempts.
    public Task<bool> ReleaseAsync(Guid torrentId, string leaseOwner, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return UpdateLeasedAsync(
            torrentId,
            leaseOwner,
            setters => setters
                .SetProperty(t => t.Status, TorrentStatus.Pending)
                .SetProperty(t => t.Attempts, t => t.Attempts > 0 ? t.Attempts - 1 : 0)
                .SetProperty(t => t.LeaseOwner, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(t => t.HashedBytes, 0)
                .SetProperty(t => t.UpdatedAt, now),
            torrent =>
            {
                torrent.Status = TorrentStatus.Pending;
                torrent.Attempts = Math.Max(0, torrent.Attempts - 1);
                ClearLease(torrent);
                torrent.UpdatedAt = now;
            },
            cancellationToken);
    }

    // Number of Pending jobs that will be claimed before this one, given the current priorities.
    // Approximate: jobs submitted later with a higher priority can still overtake it.
    public async Task<int?> GetQueuePositionAsync(TorrentRecord torrent, CancellationToken cancellationToken)
    {
        if (torrent.Status != TorrentStatus.Pending)
            return null;

        var priority = torrent.Priority;
        var availableAt = torrent.AvailableAt;
        var id = torrent.Id;
        return await db.Torrents.AsNoTracking()
            .Where(t => t.Status == TorrentStatus.Pending && t.Id != id)
            .CountAsync(
                t => t.Priority > priority || (t.Priority == priority && t.AvailableAt < availableAt),
                cancellationToken);
    }

    public static void ClearLease(TorrentRecord torrent)
    {
        torrent.LeaseOwner = null;
        torrent.LeaseExpiresAt = null;
        torrent.HashedBytes = 0;
    }

    async Task<bool> UpdateLeasedAsync(
        Guid torrentId,
        string leaseOwner,
        Action<UpdateSettersBuilder<TorrentRecord>> setters,
        Action<TorrentRecord> apply,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var updated = await db.Torrents
                .Where(t => t.Id == torrentId && t.Status == TorrentStatus.Hashing && t.LeaseOwner == leaseOwner)
                .ExecuteUpdateAsync(setters, cancellationToken);
            return updated == 1;
        }

        var torrent = await db.Torrents.FirstOrDefaultAsync(
            t => t.Id == torrentId && t.Status == TorrentStatus.Hashing && t.LeaseOwner == leaseOwner,
            cancellationToken);
        if (torrent is null)
            return false;

        apply(torrent);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // EF InMemory has no row locks; tests run a single worker against it.
    async Task<TorrentRecord?> ClaimNextInMemoryAsync(
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents
                .Where(t => t.Status == TorrentStatus.Hashing && (t.LeaseExpiresAt == null || t.LeaseExpiresAt < now))
                .OrderBy(t => t.LeaseExpiresAt)
                .FirstOrDefaultAsync(cancellationToken)
            ?? await db.Torrents
                .Where(t => t.Status == TorrentStatus.Pending && t.AvailableAt <= now)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.AvailableAt)
                .ThenBy(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        if (torrent is null)
            return null;

        torrent.Status = TorrentStatus.Hashing;
        torrent.LeaseOwner = leaseOwner;
        torrent.LeaseExpiresAt = leaseExpiresAt;
        torrent.Attempts++;
        torrent.HashedBytes = 0;
        torrent.Error = null;
        torrent.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return torrent;
    }
}

// Wakes idle workers when a job is submitted to this process. Workers also poll the database, so
// jobs from other instances, retries past their backoff and missed signals are still picked up.
public sealed class IndexingSignal
{
    const int MaxBufferedSignals = 64;

    readonly SemaphoreSlim signals = new(0);

    public void Notify()
    {
        if (signals.CurrentCount < MaxBufferedSignals)
            signals.Release();
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => signals.WaitAsync(timeout, cancellationToken);
}
