using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Tests;

public sealed class IndexingQueueTests
{
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ClaimNext_Takes_Highest_Priority_Then_Oldest()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var oldLow = AddTorrent(db, TorrentStatus.Pending, priority: 0, availableAt: now.AddMinutes(-10));
        var newHigh = AddTorrent(db, TorrentStatus.Pending, priority: 5, availableAt: now.AddMinutes(-1));
        var oldHigh = AddTorrent(db, TorrentStatus.Pending, priority: 5, availableAt: now.AddMinutes(-5));
        _ = AddTorrent(db, TorrentStatus.Ready, priority: 100, availableAt: now.AddMinutes(-20));
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);

        Assert.Equal(oldHigh.Id, (await queue.ClaimNextAsync("a", Lease, CancellationToken.None))!.Id);
        Assert.Equal(newHigh.Id, (await queue.ClaimNextAsync("b", Lease, CancellationToken.None))!.Id);
        Assert.Equal(oldLow.Id, (await queue.ClaimNextAsync("c", Lease, CancellationToken.None))!.Id);
        Assert.Null(await queue.ClaimNextAsync("d", Lease, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNext_Sets_Lease_And_Counts_Attempt()
    {
        await using var db = CreateDb();
        var pending = AddTorrent(db, TorrentStatus.Pending, attempts: 2);
        await db.SaveChangesAsync();

        var claimed = await new IndexingQueue(db).ClaimNextAsync("owner", Lease, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(pending.Id, claimed.Id);
        Assert.Equal(TorrentStatus.Hashing, claimed.Status);
        Assert.Equal(3, claimed.Attempts);
        Assert.Equal("owner", claimed.LeaseOwner);
        Assert.True(claimed.LeaseExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ClaimNext_Skips_Jobs_Still_Backing_Off()
    {
        await using var db = CreateDb();
        AddTorrent(db, TorrentStatus.Pending, availableAt: DateTimeOffset.UtcNow.AddMinutes(5));
        await db.SaveChangesAsync();

        Assert.Null(await new IndexingQueue(db).ClaimNextAsync("owner", Lease, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNext_Reclaims_Expired_Leases_But_Not_Live_Ones()
    {
        await using var db = CreateDb();
        var live = AddTorrent(db, TorrentStatus.Hashing);
        live.LeaseOwner = "live";
        live.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var expired = AddTorrent(db, TorrentStatus.Hashing, attempts: 1);
        expired.LeaseOwner = "dead";
        expired.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);

        var claimed = await queue.ClaimNextAsync("new", Lease, CancellationToken.None);

        Assert.Equal(expired.Id, claimed!.Id);
        Assert.Equal("new", claimed.LeaseOwner);
        Assert.Equal(2, claimed.Attempts);
        Assert.Null(await queue.ClaimNextAsync("other", Lease, CancellationToken.None));
    }

    [Fact]
    public async Task Writes_Are_Fenced_On_The_Lease_Owner()
    {
        await using var db = CreateDb();
        AddTorrent(db, TorrentStatus.Pending);
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);
        var claimed = await queue.ClaimNextAsync("owner", Lease, CancellationToken.None);

        Assert.False(await queue.RenewLeaseAsync(claimed!.Id, "stale", Lease, 10, CancellationToken.None));
        Assert.False(await queue.FailAsync(claimed.Id, "stale", "nope", CancellationToken.None));
        Assert.True(await queue.RenewLeaseAsync(claimed.Id, "owner", Lease, 10, CancellationToken.None));

        var stored = await db.Torrents.SingleAsync();
        Assert.Equal(TorrentStatus.Hashing, stored.Status);
        Assert.Equal(10, stored.HashedBytes);
    }

    [Fact]
    public async Task Requeue_Clears_Lease_And_Delays_Next_Claim()
    {
        await using var db = CreateDb();
        AddTorrent(db, TorrentStatus.Pending);
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);
        var claimed = await queue.ClaimNextAsync("owner", Lease, CancellationToken.None);

        Assert.True(await queue.RequeueAsync(claimed!.Id, "owner", DateTimeOffset.UtcNow.AddMinutes(1), "flaky", CancellationToken.None));

        var stored = await db.Torrents.SingleAsync();
        Assert.Equal(TorrentStatus.Pending, stored.Status);
        Assert.Null(stored.LeaseOwner);
        Assert.Equal("flaky", stored.Error);
        Assert.Null(await queue.ClaimNextAsync("owner", Lease, CancellationToken.None));
    }

    [Fact]
    public async Task Release_Returns_Job_Without_Spending_An_Attempt()
    {
        await using var db = CreateDb();
        AddTorrent(db, TorrentStatus.Pending, attempts: 1);
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);
        var claimed = await queue.ClaimNextAsync("owner", Lease, CancellationToken.None);

        Assert.True(await queue.ReleaseAsync(claimed!.Id, "owner", CancellationToken.None));

        var stored = await db.Torrents.SingleAsync();
        Assert.Equal(TorrentStatus.Pending, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Null(stored.LeaseOwner);
    }

    [Fact]
    public async Task Queue_Position_Counts_Jobs_Ahead()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var high = AddTorrent(db, TorrentStatus.Pending, priority: 10, availableAt: now);
        var first = AddTorrent(db, TorrentStatus.Pending, availableAt: now.AddMinutes(-2));
        var second = AddTorrent(db, TorrentStatus.Pending, availableAt: now.AddMinutes(-1));
        var hashing = AddTorrent(db, TorrentStatus.Hashing, priority: 50);
        await db.SaveChangesAsync();
        var queue = new IndexingQueue(db);

        Assert.Equal(0, await queue.GetQueuePositionAsync(high, CancellationToken.None));
        Assert.Equal(1, await queue.GetQueuePositionAsync(first, CancellationToken.None));
        Assert.Equal(2, await queue.GetQueuePositionAsync(second, CancellationToken.None));
        Assert.Null(await queue.GetQueuePositionAsync(hashing, CancellationToken.None));
    }

    [Fact]
    public async Task Signal_Wakes_A_Waiter_And_Times_Out_Otherwise()
    {
        var signal = new IndexingSignal();
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None));

        signal.Notify();
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    static IcecoldDbContext CreateDb()
        => new(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

    static TorrentRecord AddTorrent(
        IcecoldDbContext db,
        TorrentStatus status,
        int attempts = 0,
        int priority = 0,
        DateTimeOffset? availableAt = null)
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
            Priority = priority,
            AvailableAt = availableAt ?? now.AddSeconds(-1),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Torrents.Add(torrent);
        return torrent;
    }
}
