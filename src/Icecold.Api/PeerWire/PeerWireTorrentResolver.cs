using System.Diagnostics;
using Icecold.Api.Torrents;
using Icecold.Api.Content;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireTorrentResolver(TorrentLocationService locations)
{
    const int MaxCacheEntries = 1024;

    readonly object gate = new();
    readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    // Every peer-wire connection would otherwise cost a DB query, a content stat and a SHA-1 of
    // the info dictionary. Results (including misses) are reused briefly; a location that fails
    // to read is invalidated by the handler so the next connection re-resolves and fails over.
    internal TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<PeerWireTorrentContext?> ResolveAsync(string infoHashHex, CancellationToken cancellationToken)
    {
        var normalized = infoHashHex.ToLowerInvariant();
        var key = InfoHashKey(normalized);
        if (TryGetCached(key, out var cached))
            return cached;

        var resolved = await locations.ResolveByInfoHashAsync(normalized, cancellationToken);
        var context = BuildContext(resolved, normalized);
        Store(key, context);
        return context;
    }

    public async Task<PeerWireTorrentContext?> ResolveByMseObfuscatedHashAsync(string obfuscatedHashHex, CancellationToken cancellationToken)
    {
        var normalized = obfuscatedHashHex.ToLowerInvariant();
        var key = MseHashKey(normalized);
        if (TryGetCached(key, out var cached))
            return cached;

        var resolved = await locations.ResolveByMseObfuscatedHashAsync(normalized, cancellationToken);
        var context = BuildContext(resolved, resolved?.Torrent.InfoHashHex?.ToLowerInvariant());
        Store(key, context);

        // The plaintext handshake inside the MSE stream resolves the same torrent by infohash next.
        if (context is not null)
            Store(InfoHashKey(context.InfoHashHex.ToLowerInvariant()), context);

        return context;
    }

    public void Invalidate(string infoHashHex)
    {
        var normalized = infoHashHex.ToLowerInvariant();
        lock (gate)
        {
            cache.Remove(InfoHashKey(normalized));
            cache.Remove(MseHashKey(PeerWireMse.HashReq2Hex(normalized)));
        }
    }

    static string InfoHashKey(string infoHashHex)
        => "i:" + infoHashHex;

    static string MseHashKey(string obfuscatedHashHex)
        => "m:" + obfuscatedHashHex;

    bool TryGetCached(string key, out PeerWireTorrentContext? context)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out var entry))
            {
                if (Stopwatch.GetTimestamp() < entry.ExpiresAtTimestamp)
                {
                    context = entry.Context;
                    return true;
                }

                cache.Remove(key);
            }
        }

        context = null;
        return false;
    }

    void Store(string key, PeerWireTorrentContext? context)
    {
        if (CacheTtl <= TimeSpan.Zero)
            return;

        var now = Stopwatch.GetTimestamp();
        var expiresAt = now + (long)(CacheTtl.TotalSeconds * Stopwatch.Frequency);
        lock (gate)
        {
            if (cache.Count >= MaxCacheEntries && !cache.ContainsKey(key))
            {
                foreach (var expired in cache.Where(pair => pair.Value.ExpiresAtTimestamp <= now).Select(pair => pair.Key).ToList())
                    cache.Remove(expired);

                // Still full of live entries (e.g. a scan of random infohashes): start over rather
                // than tracking recency; the cache is only a short-lived optimisation.
                if (cache.Count >= MaxCacheEntries)
                    cache.Clear();
            }

            cache[key] = new CacheEntry(context, expiresAt);
        }
    }

    static PeerWireTorrentContext? BuildContext(
        TorrentServingLocation? resolved,
        string? expectedInfoHashHex)
    {
        var torrent = resolved?.Torrent;
        if (torrent is null || torrent.PieceLength is null || torrent.PieceCount is null || torrent.TorrentBytes is null)
            return null;

        if (!PeerWireBencode.TryExtractTorrentInfo(torrent.TorrentBytes, out var infoBytes))
            return null;

        if (!string.Equals(InfoHashUtil.Sha1Hex(infoBytes), expectedInfoHashHex, StringComparison.Ordinal))
            return null;

        return new PeerWireTorrentContext(
            torrent.InfoHashHex!,
            resolved!.Source,
            resolved.Metadata.Path,
            torrent.ContentLength,
            torrent.PieceLength.Value,
            torrent.PieceCount.Value,
            infoBytes);
    }

    sealed record CacheEntry(PeerWireTorrentContext? Context, long ExpiresAtTimestamp);
}

public sealed record PeerWireTorrentContext(
    string InfoHashHex,
    IContentSource Source,
    string SourcePath,
    long ContentLength,
    int PieceLength,
    int PieceCount,
    byte[] InfoBytes);
