using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Indexing;

public sealed class IndexingClaimService(IcecoldDbContext db)
{
    public async Task<TorrentRecord?> ClaimForHashingAsync(
        Guid torrentId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            var claimed = await db.Torrents
                .Where(t => t.Id == torrentId && t.Status == TorrentStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.Status, TorrentStatus.Hashing)
                        .SetProperty(t => t.Attempts, t => t.Attempts + 1)
                        .SetProperty(t => t.Error, (string?)null)
                        .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken);

            if (claimed == 0)
                return null;

            return await db.Torrents.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
        }

        var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
        if (torrent is null || torrent.Status != TorrentStatus.Pending)
            return null;

        torrent.Status = TorrentStatus.Hashing;
        torrent.Attempts++;
        torrent.Error = null;
        torrent.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return torrent;
    }

    public async Task<IReadOnlyList<Guid>> ResetInterruptedAndListPendingAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            await db.Torrents
                .Where(t => t.Status == TorrentStatus.Hashing)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.Status, TorrentStatus.Pending)
                        .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken);

            return await db.Torrents.AsNoTracking()
                .Where(t => t.Status == TorrentStatus.Pending)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var interrupted = await db.Torrents
            .Where(t => t.Status == TorrentStatus.Hashing)
            .ToListAsync(cancellationToken);

        foreach (var torrent in interrupted)
        {
            torrent.Status = TorrentStatus.Pending;
            torrent.UpdatedAt = now;
        }

        if (interrupted.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        return await db.Torrents.AsNoTracking()
            .Where(t => t.Status == TorrentStatus.Pending)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }
}
