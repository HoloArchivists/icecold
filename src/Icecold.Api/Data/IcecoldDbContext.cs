using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Data;

public sealed class IcecoldDbContext(DbContextOptions<IcecoldDbContext> options) : DbContext(options)
{
    public DbSet<TorrentRecord> Torrents => Set<TorrentRecord>();

    public DbSet<TorrentLocationRecord> TorrentLocations => Set<TorrentLocationRecord>();

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
    }
}
