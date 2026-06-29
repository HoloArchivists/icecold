using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Tracker;

public sealed class TrackerAnnounceService(IcecoldDbContext db, ITrackerPeerStore peerStore)
{
    public async Task<TrackerAnnounceServiceResult> AnnounceAsync(
        TrackerAnnounceInput input,
        CancellationToken cancellationToken)
    {
        var exists = await db.Torrents.AsNoTracking()
            .AnyAsync(t => t.InfoHashHex == input.InfoHashHex && t.Status == TorrentStatus.Ready, cancellationToken);

        if (!exists)
            return TrackerAnnounceServiceResult.Failure("unregistered torrent");

        return TrackerAnnounceServiceResult.Success(peerStore.Announce(input));
    }
}

public sealed record TrackerAnnounceServiceResult(TrackerAnnounceResult? Announce, string? FailureReason)
{
    public bool Succeeded => Announce is not null;

    public static TrackerAnnounceServiceResult Success(TrackerAnnounceResult announce)
        => new(announce, null);

    public static TrackerAnnounceServiceResult Failure(string failureReason)
        => new(null, failureReason);
}
