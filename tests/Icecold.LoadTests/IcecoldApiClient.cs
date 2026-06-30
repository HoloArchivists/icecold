using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Icecold.LoadTests;

sealed class IcecoldApiClient(HttpClient http, string adminApiKey)
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IndexResponse> IndexFileAsync(
        string source,
        string path,
        string? displayName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/file")
        {
            Content = JsonContent.Create(new { source, path, displayName }, options: JsonOptions)
        };
        request.Headers.Add("X-Icecold-Admin-Key", adminApiKey);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IndexResponse>(JsonOptions, cancellationToken))!;
    }

    public async Task<IndexResponse> GetTorrentAsync(Guid id, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/torrents/{id}");
        request.Headers.Add("X-Icecold-Admin-Key", adminApiKey);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IndexResponse>(JsonOptions, cancellationToken))!;
    }

    public async Task<ReadyTorrent> PollReadyAsync(
        IndexResponse submitted,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var current = await GetTorrentAsync(submitted.Id, cancellationToken);
            if (current.Status is "Ready" or "Duplicate")
            {
                if (string.IsNullOrWhiteSpace(current.InfoHash) || current.PieceLength is null || current.PieceCount is null)
                    throw new InvalidOperationException($"Torrent {current.Id} reached {current.Status} without complete torrent metadata.");

                return new ReadyTorrent(
                    current.Id,
                    current.DisplayName,
                    current.ContentLength,
                    current.InfoHash,
                    current.PieceLength.Value,
                    current.PieceCount.Value,
                    stopwatch.Elapsed);
            }

            if (current.Status == "Failed")
                throw new InvalidOperationException($"Torrent {current.Id} failed indexing: {current.Error}");

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"Torrent {submitted.Id} did not become ready within {timeout}.");
    }

    public async Task<string> GetMagnetAsync(string infoHash, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/torrents/{infoHash}/magnet", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<long> GetTorrentFileLengthAsync(string infoHash, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/torrents/{infoHash}.torrent", cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length;
    }

    public async Task<TransferMeasurement> DownloadWebSeedAsync(ReadyTorrent torrent, int clients, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, clients)
            .Select(_ => DownloadWebSeedOnceAsync(torrent, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();
        return new TransferMeasurement(results.Sum(), stopwatch.Elapsed);
    }

    async Task<long> DownloadWebSeedOnceAsync(ReadyTorrent torrent, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"/webseed/{torrent.InfoHash}/{Uri.EscapeDataString(torrent.DisplayName)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long bytes = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    return bytes;

                bytes += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<long> AnnounceStartedAsync(
        ReadyTorrent torrent,
        int port,
        int numwant,
        CancellationToken cancellationToken)
    {
        var infoHash = Convert.FromHexString(torrent.InfoHash);
        var peerId = Encoding.ASCII.GetBytes("-ICLT00-TRACKER00001");
        var url = new StringBuilder("/announce?info_hash=")
            .Append(PercentEncode(infoHash))
            .Append("&peer_id=")
            .Append(PercentEncode(peerId))
            .Append("&port=")
            .Append(port)
            .Append("&uploaded=0&downloaded=0&left=")
            .Append(torrent.ContentLength)
            .Append("&compact=1&numwant=")
            .Append(numwant)
            .Append("&event=started");

        using var response = await http.GetAsync(url.ToString(), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return body.Length;
    }

    static string PercentEncode(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
            builder.Append('%').Append(value.ToString("X2"));

        return builder.ToString();
    }
}

sealed record IndexResponse(
    Guid Id,
    string Source,
    string Path,
    string DisplayName,
    long ContentLength,
    string Status,
    string? InfoHash,
    Guid? DuplicateOfId,
    int? PieceLength,
    int? PieceCount,
    string? Error);

sealed record ReadyTorrent(
    Guid Id,
    string DisplayName,
    long ContentLength,
    string InfoHash,
    int PieceLength,
    int PieceCount,
    TimeSpan ReadyLatency);

sealed record TransferMeasurement(long Bytes, TimeSpan Elapsed)
{
    public double MiBPerSecond => Bytes / 1024d / 1024d / Math.Max(Elapsed.TotalSeconds, 0.000001);
}
