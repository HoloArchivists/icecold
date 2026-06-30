using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Tracker;

public sealed class TrackerPeerPruningService(
    InMemoryTrackerPeerStore peerStore,
    IOptions<IcecoldOptions> options,
    ILogger<TrackerPeerPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Tracker.PruneIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var result = peerStore.PruneExpired(DateTimeOffset.UtcNow);
                if (result.RemovedPeers > 0 || result.RemovedTorrents > 0)
                {
                    logger.LogDebug(
                        "Pruned {RemovedPeers} expired tracker peers across {RemovedTorrents} infohash buckets.",
                        result.RemovedPeers,
                        result.RemovedTorrents);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
