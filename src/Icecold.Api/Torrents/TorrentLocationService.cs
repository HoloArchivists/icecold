using System.Linq.Expressions;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Torrents;

public sealed class TorrentLocationService(
    IServiceScopeFactory scopeFactory,
    ContentSourceRegistry sources,
    TorrentBuilder torrentBuilder,
    ILogger<TorrentLocationService> logger)
{
    static readonly TimeSpan HealthyVerificationWriteInterval = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<TorrentLocationResponse>?> GetLocationsAsync(
        Guid torrentId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await LoadCanonicalTorrentAsync(db, torrentId, includeLocations: true, cancellationToken);
        if (torrent is null)
            return null;

        return torrent.Locations
            .OrderByDescending(l => l.IsPrimary)
            .ThenBy(l => l.Priority)
            .ThenBy(l => l.CreatedAt)
            .Select(TorrentLocationResponse.From)
            .ToList();
    }

    public async Task<TorrentLocationOperationResult> AddLocationAsync(
        Guid torrentId,
        AddTorrentLocationRequest request,
        CancellationToken cancellationToken)
    {
        var requestedSource = request.Source.Trim();
        var requestedPath = request.Path.Trim();
        if (string.IsNullOrWhiteSpace(requestedSource) || string.IsNullOrWhiteSpace(requestedPath))
            return BadRequest("Source and path are required.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await LoadCanonicalTorrentAsync(db, torrentId, includeLocations: true, cancellationToken);
        if (torrent is null)
            return NotFound();

        if (torrent.Status != TorrentStatus.Ready || torrent.InfoHashHex is null || torrent.TorrentBytes is null)
            return Conflict("Locations can only be added to a ready torrent.");

        IContentSource source;
        ContentMetadata metadata;
        try
        {
            source = sources.GetRequired(requestedSource);
            metadata = await source.GetMetadataAsync(requestedPath, cancellationToken);
        }
        catch (ContentSourceNotFoundException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ContentItemNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (IsContentAccessFailure(ex))
        {
            return BadRequest(ex.Message);
        }

        TorrentBuildResult built;
        try
        {
            built = await torrentBuilder.BuildSingleFileAsync(metadata, source, torrent.DisplayName, cancellationToken);
        }
        catch (Exception ex) when (IsContentAccessFailure(ex))
        {
            return BadRequest(ex.Message);
        }

        if (!string.Equals(built.InfoHashHex, torrent.InfoHashHex, StringComparison.Ordinal))
            return Conflict("The requested location does not produce the same torrent info hash.");

        var now = DateTimeOffset.UtcNow;
        var location = torrent.Locations.FirstOrDefault(l =>
            string.Equals(l.SourceName, metadata.SourceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.SourcePath, metadata.Path, StringComparison.Ordinal)
            && l.ContentLength == metadata.Length
            && string.Equals(l.ContentVersion, metadata.Version, StringComparison.Ordinal));

        if (location is null)
        {
            var priority = request.Priority ?? (request.MakePrimary ? 0 : NextLocationPriority(torrent.Locations));
            location = new TorrentLocationRecord
            {
                Id = Guid.NewGuid(),
                TorrentId = torrent.Id,
                SourceName = metadata.SourceName,
                SourcePath = metadata.Path,
                ContentLength = metadata.Length,
                ContentVersion = metadata.Version,
                ContentLastModified = metadata.LastModified,
                Status = TorrentLocationStatus.Active,
                IsPrimary = false,
                Priority = priority,
                LastVerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.TorrentLocations.Add(location);
            if (!torrent.Locations.Any(l => l.Id == location.Id))
                torrent.Locations.Add(location);
        }
        else
        {
            location.ContentLength = metadata.Length;
            location.ContentVersion = metadata.Version;
            location.ContentLastModified = metadata.LastModified;
            location.Status = TorrentLocationStatus.Active;
            location.LastVerifiedAt = now;
            location.LastError = null;
            location.UpdatedAt = now;
            if (request.Priority.HasValue)
                location.Priority = request.Priority.Value;
        }

        if (request.MakePrimary || !torrent.Locations.Any(l => l.Id != location.Id && l.Status == TorrentLocationStatus.Active && l.IsPrimary))
            MakePrimary(torrent.Locations, location, now);

        await db.SaveChangesAsync(cancellationToken);
        return Success(location);
    }

    public async Task<TorrentLocationOperationResult> SetPrimaryAsync(
        Guid torrentId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await LoadCanonicalTorrentAsync(db, torrentId, includeLocations: true, cancellationToken);
        if (torrent is null)
            return NotFound();

        var location = torrent.Locations.FirstOrDefault(l => l.Id == locationId);
        if (location is null)
            return NotFound();

        if (location.Status != TorrentLocationStatus.Active)
            return Conflict("Only active locations can be made primary.");

        var now = DateTimeOffset.UtcNow;
        var verificationError = await VerifyLocationMetadataAsync(location, now, cancellationToken);
        if (verificationError is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Conflict(verificationError);
        }

        MakePrimary(torrent.Locations, location, now);
        await db.SaveChangesAsync(cancellationToken);
        return Success(location);
    }

    public async Task<TorrentLocationOperationResult> DisableAsync(
        Guid torrentId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var torrent = await LoadCanonicalTorrentAsync(db, torrentId, includeLocations: true, cancellationToken);
        if (torrent is null)
            return NotFound();

        var location = torrent.Locations.FirstOrDefault(l => l.Id == locationId);
        if (location is null)
            return NotFound();

        var now = DateTimeOffset.UtcNow;
        location.Status = TorrentLocationStatus.Disabled;
        location.IsPrimary = false;
        location.LastError = "Disabled by admin.";
        location.UpdatedAt = now;

        var replacement = torrent.Locations
            .Where(l => l.Id != location.Id && l.Status == TorrentLocationStatus.Active)
            .OrderByDescending(l => l.IsPrimary)
            .ThenBy(l => l.Priority)
            .ThenBy(l => l.CreatedAt)
            .FirstOrDefault();
        if (replacement is not null && !torrent.Locations.Any(l => l.Id != location.Id && l.IsPrimary && l.Status == TorrentLocationStatus.Active))
            MakePrimary(torrent.Locations, replacement, now);

        await db.SaveChangesAsync(cancellationToken);
        return Success(location);
    }

    public Task<TorrentServingLocation?> ResolveByInfoHashAsync(
        string infoHashHex,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? excludedLocationIds = null)
    {
        var normalized = infoHashHex.ToLowerInvariant();
        return ResolveAsync(
            t => t.InfoHashHex == normalized && t.Status == TorrentStatus.Ready,
            cancellationToken,
            excludedLocationIds);
    }

    public Task<TorrentServingLocation?> ResolveByMseObfuscatedHashAsync(
        string obfuscatedHashHex,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? excludedLocationIds = null)
    {
        var normalized = obfuscatedHashHex.ToLowerInvariant();
        return ResolveAsync(
            t => t.MseObfuscatedHashHex == normalized && t.Status == TorrentStatus.Ready,
            cancellationToken,
            excludedLocationIds);
    }

    public async Task ReportLocationFailureAsync(
        Guid locationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await MarkLocationUnhealthyAsync(
            locationId,
            exception is ContentItemNotFoundException or ContentSourceNotFoundException
                ? TorrentLocationStatus.Missing
                : TorrentLocationStatus.Stale,
            exception.Message,
            cancellationToken);
    }

    public async Task<bool> ReadyTorrentExistsByInfoHashAsync(
        string infoHashHex,
        CancellationToken cancellationToken)
    {
        var normalized = infoHashHex.ToLowerInvariant();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        return await db.Torrents.AsNoTracking()
            .AnyAsync(t => t.InfoHashHex == normalized && t.Status == TorrentStatus.Ready, cancellationToken);
    }

    async Task<TorrentServingLocation?> ResolveAsync(
        Expression<Func<TorrentRecord, bool>> predicate,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? excludedLocationIds)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var readyTorrent = await db.Torrents.AsNoTracking()
            .Include(t => t.Locations)
            .FirstOrDefaultAsync(predicate, cancellationToken);
        if (readyTorrent is null)
            return null;

        foreach (var location in CandidateLocations(readyTorrent, excludedLocationIds))
        {
            try
            {
                var source = sources.GetRequired(location.SourceName);
                var metadata = await source.GetMetadataAsync(location.SourcePath, cancellationToken);
                if (metadata.Length != location.ContentLength || metadata.Version != location.ContentVersion)
                {
                    await MarkLocationUnhealthyAsync(
                        location.Id,
                        TorrentLocationStatus.Stale,
                        "Backing content changed since this location was verified.",
                        cancellationToken);
                    continue;
                }

                if (ShouldRefreshHealthyLocation(location))
                    await MarkLocationHealthyAsync(location.Id, cancellationToken);

                return new TorrentServingLocation(readyTorrent, location, source, metadata);
            }
            catch (Exception ex) when (IsContentAccessFailure(ex))
            {
                logger.LogWarning(
                    ex,
                    "Skipping unavailable location {LocationId} for torrent {TorrentId}",
                    location.Id,
                    readyTorrent.Id);

                await MarkLocationUnhealthyAsync(
                    location.Id,
                    ex is ContentItemNotFoundException or ContentSourceNotFoundException
                        ? TorrentLocationStatus.Missing
                        : TorrentLocationStatus.Stale,
                    ex.Message,
                    cancellationToken);
            }
        }

        return null;
    }

    static IEnumerable<TorrentLocationRecord> CandidateLocations(
        TorrentRecord torrent,
        IReadOnlySet<Guid>? excludedLocationIds)
    {
        var candidates = torrent.Locations
            .Where(l => l.Status != TorrentLocationStatus.Disabled)
            .Where(l => excludedLocationIds is null || !excludedLocationIds.Contains(l.Id))
            .OrderByDescending(l => l.Status == TorrentLocationStatus.Active)
            .ThenByDescending(l => l.IsPrimary)
            .ThenBy(l => l.Priority)
            .ThenBy(l => l.CreatedAt)
            .ToList();

        if (candidates.Count > 0)
            return candidates;

        if (torrent.Locations.Count > 0)
            return [];

        if (string.IsNullOrWhiteSpace(torrent.SourceName)
            || string.IsNullOrWhiteSpace(torrent.SourcePath)
            || excludedLocationIds?.Contains(Guid.Empty) == true)
            return [];

        return
        [
            new TorrentLocationRecord
            {
                Id = Guid.Empty,
                TorrentId = torrent.Id,
                SourceName = torrent.SourceName,
                SourcePath = torrent.SourcePath,
                ContentLength = torrent.ContentLength,
                ContentVersion = torrent.ContentVersion,
                ContentLastModified = torrent.ContentLastModified,
                Status = TorrentLocationStatus.Active,
                IsPrimary = true,
                Priority = 0,
                CreatedAt = torrent.CreatedAt,
                UpdatedAt = torrent.UpdatedAt
            }
        ];
    }

    async Task MarkLocationHealthyAsync(Guid locationId, CancellationToken cancellationToken)
    {
        if (locationId == Guid.Empty)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var location = await db.TorrentLocations.FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken);
        if (location is null)
            return;

        location.Status = TorrentLocationStatus.Active;
        location.LastVerifiedAt = DateTimeOffset.UtcNow;
        location.LastError = null;
        location.UpdatedAt = location.LastVerifiedAt.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    async Task MarkLocationUnhealthyAsync(
        Guid locationId,
        TorrentLocationStatus status,
        string error,
        CancellationToken cancellationToken)
    {
        if (locationId == Guid.Empty)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
        var location = await db.TorrentLocations.FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken);
        if (location is null || location.Status == TorrentLocationStatus.Disabled)
            return;

        location.Status = status;
        location.LastError = error;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    static async Task<TorrentRecord?> LoadCanonicalTorrentAsync(
        IcecoldDbContext db,
        Guid torrentId,
        bool includeLocations,
        CancellationToken cancellationToken)
    {
        IQueryable<TorrentRecord> query = db.Torrents;
        if (includeLocations)
            query = query.Include(t => t.Locations);

        var torrent = await query.FirstOrDefaultAsync(t => t.Id == torrentId, cancellationToken);
        if (torrent?.DuplicateOfId is not { } canonicalId)
            return torrent;

        query = db.Torrents;
        if (includeLocations)
            query = query.Include(t => t.Locations);

        return await query.FirstOrDefaultAsync(t => t.Id == canonicalId, cancellationToken);
    }

    static void MakePrimary(IEnumerable<TorrentLocationRecord> locations, TorrentLocationRecord primary, DateTimeOffset now)
    {
        foreach (var location in locations)
        {
            location.IsPrimary = location.Id == primary.Id;
            location.UpdatedAt = now;
        }

        primary.Priority = 0;
    }

    async Task<string?> VerifyLocationMetadataAsync(
        TorrentLocationRecord location,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = sources.GetRequired(location.SourceName);
            var metadata = await source.GetMetadataAsync(location.SourcePath, cancellationToken);
            if (metadata.Length != location.ContentLength || metadata.Version != location.ContentVersion)
            {
                location.Status = TorrentLocationStatus.Stale;
                location.LastError = "Backing content changed since this location was verified.";
                location.UpdatedAt = now;
                return location.LastError;
            }

            location.ContentLastModified = metadata.LastModified;
            location.Status = TorrentLocationStatus.Active;
            location.LastVerifiedAt = now;
            location.LastError = null;
            location.UpdatedAt = now;
            return null;
        }
        catch (Exception ex) when (IsContentAccessFailure(ex))
        {
            location.Status = ex is ContentItemNotFoundException or ContentSourceNotFoundException
                ? TorrentLocationStatus.Missing
                : TorrentLocationStatus.Stale;
            location.LastError = ex.Message;
            location.UpdatedAt = now;
            return ex.Message;
        }
    }

    static int NextLocationPriority(IEnumerable<TorrentLocationRecord> locations)
    {
        var max = locations.Select(l => (int?)l.Priority).Max() ?? 0;
        return max + 10;
    }

    static bool IsContentAccessFailure(Exception ex)
        => ex is ContentSourceException or IOException or UnauthorizedAccessException;

    static bool ShouldRefreshHealthyLocation(TorrentLocationRecord location)
        => location.Status != TorrentLocationStatus.Active
            || location.LastError is not null
            || location.LastVerifiedAt is null
            || DateTimeOffset.UtcNow - location.LastVerifiedAt.Value > HealthyVerificationWriteInterval;

    static TorrentLocationOperationResult Success(TorrentLocationRecord location)
        => new(TorrentLocationOperationStatus.Success, TorrentLocationResponse.From(location), null);

    static TorrentLocationOperationResult NotFound(string? error = null)
        => new(TorrentLocationOperationStatus.NotFound, null, error);

    static TorrentLocationOperationResult BadRequest(string error)
        => new(TorrentLocationOperationStatus.BadRequest, null, error);

    static TorrentLocationOperationResult Conflict(string error)
        => new(TorrentLocationOperationStatus.Conflict, null, error);
}
