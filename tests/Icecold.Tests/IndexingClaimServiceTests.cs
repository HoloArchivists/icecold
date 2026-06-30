using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Tests;

public sealed class IndexingClaimServiceTests
{
    [Fact]
    public async Task ClaimForHashingAsync_Only_Claims_Pending_Rows()
    {
        await using var db = CreateDb();
        var pending = AddTorrent(db, TorrentStatus.Pending, attempts: 2);
        var hashing = AddTorrent(db, TorrentStatus.Hashing, attempts: 5);
        var ready = AddTorrent(db, TorrentStatus.Ready, attempts: 7);
        await db.SaveChangesAsync();
        var service = new IndexingClaimService(db);

        var claimed = await service.ClaimForHashingAsync(pending.Id, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(TorrentStatus.Hashing, claimed.Status);
        Assert.Equal(3, claimed.Attempts);
        Assert.Null(await service.ClaimForHashingAsync(pending.Id, CancellationToken.None));
        Assert.Null(await service.ClaimForHashingAsync(hashing.Id, CancellationToken.None));
        Assert.Null(await service.ClaimForHashingAsync(ready.Id, CancellationToken.None));

        var storedPending = await db.Torrents.FindAsync(pending.Id);
        Assert.Equal(TorrentStatus.Hashing, storedPending!.Status);
        Assert.Equal(3, storedPending.Attempts);
    }

    [Fact]
    public async Task ResetInterruptedAndListPendingAsync_Requeues_Hashing_As_Pending()
    {
        await using var db = CreateDb();
        var pending = AddTorrent(db, TorrentStatus.Pending);
        var hashing = AddTorrent(db, TorrentStatus.Hashing);
        _ = AddTorrent(db, TorrentStatus.Ready);
        await db.SaveChangesAsync();
        var service = new IndexingClaimService(db);

        var ids = await service.ResetInterruptedAndListPendingAsync(CancellationToken.None);

        Assert.Contains(pending.Id, ids);
        Assert.Contains(hashing.Id, ids);
        Assert.Equal(TorrentStatus.Pending, (await db.Torrents.FindAsync(hashing.Id))!.Status);
    }

    static IcecoldDbContext CreateDb()
        => new(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

    static TorrentRecord AddTorrent(IcecoldDbContext db, TorrentStatus status, int attempts = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var torrent = new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = $"{Guid.NewGuid():n}.bin",
            DisplayName = "payload.bin",
            ContentLength = 1,
            ContentVersion = Guid.NewGuid().ToString("n"),
            Status = status,
            Attempts = attempts,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Torrents.Add(torrent);
        return torrent;
    }
}
