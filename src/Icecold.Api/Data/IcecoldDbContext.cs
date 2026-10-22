using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Icecold.Api.Data;

public sealed class IcecoldDbContext(DbContextOptions<IcecoldDbContext> options) : DbContext(options)
{
    public DbSet<TorrentRecord> Torrents => Set<TorrentRecord>();

    public DbSet<TorrentLocationRecord> TorrentLocations => Set<TorrentLocationRecord>();

    public DbSet<TorrentFileRecord> TorrentFiles => Set<TorrentFileRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var torrent = modelBuilder.Entity<TorrentRecord>();
        torrent.ToTable("torrents");
        torrent.HasKey(t => t.Id);
        torrent.Property(t => t.SourceName).HasMaxLength(128).IsRequired();
        torrent.Property(t => t.SourcePath).HasMaxLength(4096).IsRequired();
        torrent.Property(t => t.DisplayName).HasMaxLength(512).IsRequired();
        torrent.Property(t => t.ContentVersion).HasMaxLength(512);
        torrent.Property(t => t.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        torrent.Property(t => t.InfoHashHex).HasMaxLength(40);
        torrent.Property(t => t.MseObfuscatedHashHex).HasMaxLength(40);
        torrent.Property(t => t.TorrentBytes);
        torrent.Property(t => t.Error).HasMaxLength(4096);
        torrent.Property(t => t.LeaseOwner).HasMaxLength(128);
        torrent.HasIndex(t => t.InfoHashHex)
            .IsUnique()
            .HasFilter("\"InfoHashHex\" IS NOT NULL AND \"Status\" = 'Ready'");
        torrent.HasIndex(t => t.MseObfuscatedHashHex)
            .IsUnique()
            .HasFilter("\"MseObfuscatedHashHex\" IS NOT NULL AND \"Status\" = 'Ready'");
        torrent.HasIndex(t => new { t.SourceName, t.SourcePath, t.ContentLength, t.ContentVersion })
            .IsUnique()
            .HasDatabaseName("IX_torrents_ContentIdentity")
            .HasFilter("\"ContentVersion\" IS NOT NULL");
        torrent.HasIndex(t => t.DuplicateOfId);
        torrent.HasIndex(t => t.Status);
        torrent.HasIndex(t => new { t.SourceName, t.SourcePath });
        torrent.HasIndex(t => new { t.Priority, t.AvailableAt, t.Id })
            .IsDescending(true, false, false)
            .HasDatabaseName("IX_torrents_PendingQueue")
            .HasFilter("\"Status\" = 'Pending'");
        torrent.HasIndex(t => t.LeaseExpiresAt)
            .HasDatabaseName("IX_torrents_HashingLeases")
            .HasFilter("\"Status\" = 'Hashing'");
        torrent.HasOne<TorrentRecord>()
            .WithMany()
            .HasForeignKey(t => t.DuplicateOfId)
            .OnDelete(DeleteBehavior.Restrict);

        var location = modelBuilder.Entity<TorrentLocationRecord>();
        location.ToTable("torrent_locations");
        location.HasKey(l => l.Id);
        location.Property(l => l.SourceName).HasMaxLength(128).IsRequired();
        location.Property(l => l.SourcePath).HasMaxLength(4096).IsRequired();
        location.Property(l => l.ContentVersion).HasMaxLength(512);
        location.Property(l => l.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        location.Property(l => l.LastError).HasMaxLength(4096);
        location.HasIndex(l => l.TorrentId);
        location.HasIndex(l => l.TorrentId)
            .IsUnique()
            .HasDatabaseName("IX_torrent_locations_Primary")
            .HasFilter("\"IsPrimary\" = TRUE");
        location.HasIndex(l => new { l.TorrentId, l.IsPrimary });
        location.HasIndex(l => new { l.TorrentId, l.Priority });
        location.HasIndex(l => l.Status);
        location.HasIndex(l => new { l.SourceName, l.SourcePath });
        location.HasIndex(l => new { l.TorrentId, l.SourceName, l.SourcePath, l.ContentLength, l.ContentVersion })
            .IsUnique()
            .HasDatabaseName("IX_torrent_locations_ContentIdentity")
            .HasFilter("\"ContentVersion\" IS NOT NULL");
        location.HasOne(l => l.Torrent)
            .WithMany(t => t.Locations)
            .HasForeignKey(l => l.TorrentId)
            .OnDelete(DeleteBehavior.Cascade);

        var file = modelBuilder.Entity<TorrentFileRecord>();
        file.ToTable("torrent_files");
        file.HasKey(f => f.Id);
        file.Property(f => f.SourcePath).HasMaxLength(4096).IsRequired();
        file.Property(f => f.TorrentPath).HasMaxLength(4096).IsRequired();
        file.Property(f => f.ContentVersion).HasMaxLength(512);
        file.HasIndex(f => new { f.TorrentId, f.Index }).IsUnique();
        file.HasOne<TorrentRecord>()
            .WithMany(t => t.Files)
            .HasForeignKey(f => f.TorrentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // IX_torrent_locations_Primary allows one IsPrimary row per torrent and PostgreSQL checks it
    // per statement. EF orders the UPDATEs of a single SaveChanges arbitrarily, so moving the
    // primary flag can set the new primary before clearing the old one and fail with 23505.
    // When a save both demotes and promotes primaries, write the demotions first and the
    // promotions second, inside one transaction (reusing the caller's transaction if any).
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        if (!acceptAllChangesOnSuccess)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var promotions = PendingPrimaryPromotions();
        if (promotions.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (!Database.IsRelational() || Database.CurrentTransaction is not null)
            return await SavePrimaryPromotionsLastAsync(promotions, cancellationToken);

        // No retrying execution strategy is configured; if one is added, this wrapper keeps
        // the explicit transaction legal, but a retry would replay against already-accepted state.
        return await Database.CreateExecutionStrategy().ExecuteAsync(
            promotions,
            async (_, state, ct) =>
            {
                await using var transaction = await Database.BeginTransactionAsync(ct);
                var count = await SavePrimaryPromotionsLastAsync(state, ct);
                await transaction.CommitAsync(ct);
                return count;
            },
            verifySucceeded: null,
            cancellationToken);
    }

    List<EntityEntry<TorrentLocationRecord>> PendingPrimaryPromotions()
    {
        ChangeTracker.DetectChanges();
        var locations = ChangeTracker.Entries<TorrentLocationRecord>().ToList();
        var hasDemotion = locations.Any(e =>
            e.State == EntityState.Modified
            && e.Property(l => l.IsPrimary).OriginalValue
            && !e.Entity.IsPrimary);
        if (!hasDemotion)
            return [];

        return locations
            .Where(e => e.Entity.IsPrimary
                && (e.State == EntityState.Added
                    || (e.State == EntityState.Modified && !e.Property(l => l.IsPrimary).OriginalValue)))
            .ToList();
    }

    async Task<int> SavePrimaryPromotionsLastAsync(
        List<EntityEntry<TorrentLocationRecord>> promotions,
        CancellationToken cancellationToken)
    {
        foreach (var entry in promotions)
            entry.Entity.IsPrimary = false;

        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

        foreach (var entry in promotions)
            entry.Entity.IsPrimary = true;

        await base.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
        return count;
    }
}
