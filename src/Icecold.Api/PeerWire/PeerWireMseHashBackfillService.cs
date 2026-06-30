using Icecold.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireMseHashBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<PeerWireMseHashBackfillService> logger) : BackgroundService
{
    const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var total = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
            var torrents = await db.Torrents
                .Where(t =>
                    t.Status == TorrentStatus.Ready
                    && t.InfoHashHex != null
                    && t.MseObfuscatedHashHex == null)
                .OrderBy(t => t.Id)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

            if (torrents.Count == 0)
                break;

            foreach (var torrent in torrents)
                torrent.MseObfuscatedHashHex = PeerWireMse.HashReq2Hex(torrent.InfoHashHex!);

            await db.SaveChangesAsync(stoppingToken);
            total += torrents.Count;
        }

        if (total > 0)
            logger.LogInformation("Backfilled MSE obfuscated hashes for {TorrentCount} ready torrents.", total);
    }
}
