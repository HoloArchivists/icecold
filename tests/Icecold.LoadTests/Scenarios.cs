using System.Diagnostics;

namespace Icecold.LoadTests;

static class Scenarios
{
    public static async Task<ScenarioReport> E2eSmokeAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var repoRoot = FindRepoRoot();
        var contentRoot = Path.Combine(Path.GetTempPath(), "icecold-load", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(contentRoot);

        var report = new ScenarioReport { Scenario = "e2e-smoke" };
        try
        {
            await using var process = await IcecoldProcess.StartWithPostgresAsync(
                repoRoot,
                contentRoot,
                options.GetInt32("indexing-concurrency", 2),
                cancellationToken);

            using var http = new HttpClient { BaseAddress = process.BaseUrl, Timeout = TimeSpan.FromMinutes(10) };
            var api = new IcecoldApiClient(http, "load-test-admin");
            var fileSizeBytes = options.GetInt64("file-size-mib", 32) * 1024 * 1024;
            var fixture = await GeneratedFiles.EnsureAsync(contentRoot, "e2e/payload.bin", fileSizeBytes, cancellationToken);
            var submitted = await api.IndexFileAsync("local", fixture.RelativePath, "payload.bin", cancellationToken);
            var ready = await api.PollReadyAsync(submitted, TimeSpan.FromMinutes(5), cancellationToken);

            var torrentBytes = await api.GetTorrentFileLengthAsync(ready.InfoHash, cancellationToken);
            var magnet = await api.GetMagnetAsync(ready.InfoHash, cancellationToken);
            var trackerBytes = await api.AnnounceStartedAsync(ready, port: 51413, numwant: 50, cancellationToken);
            var webSeed = await api.DownloadWebSeedAsync(ready, clients: 1, cancellationToken);
            var plaintext = await new PeerWireLoadClient(
                    "127.0.0.1",
                    process.PeerWirePort,
                    encrypted: false,
                    blockLength: options.GetInt32("block-bytes", 16 * 1024),
                    maxOutstanding: options.GetInt32("outstanding", 256))
                .DownloadAsync(ready, 0, cancellationToken);
            var mse = await new PeerWireLoadClient(
                    "127.0.0.1",
                    process.PeerWirePort,
                    encrypted: true,
                    blockLength: options.GetInt32("block-bytes", 16 * 1024),
                    maxOutstanding: options.GetInt32("outstanding", 256))
                .DownloadAsync(ready, 1, cancellationToken);

            stopwatch.Stop();
            report.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            report.Metrics["api_base_url"] = process.BaseUrl.ToString();
            report.Metrics["peer_wire_port"] = process.PeerWirePort;
            report.Metrics["file_size_bytes"] = fileSizeBytes;
            report.Metrics["file_create_seconds"] = fixture.CreateElapsed.TotalSeconds;
            report.Metrics["ready_latency_seconds"] = ready.ReadyLatency.TotalSeconds;
            report.Metrics["info_hash"] = ready.InfoHash;
            report.Metrics["torrent_bytes"] = torrentBytes;
            report.Metrics["magnet_length"] = magnet.Length;
            report.Metrics["tracker_response_bytes"] = trackerBytes;
            AddTransfer(report, "webseed", webSeed);
            AddTransfer(report, "peerwire_plaintext", plaintext);
            AddTransfer(report, "peerwire_mse", mse);
            return report;
        }
        finally
        {
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }

    public static async Task<ScenarioReport> IndexManyAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var baseUrl = options.GetUri("base-url", "http://localhost:5038");
        var contentRoot = Required(options, "content-root");
        var adminKey = options.GetString("admin-key", Environment.GetEnvironmentVariable("ICECOLD_ADMIN_API_KEY") ?? "dev-admin-key");
        var source = options.GetString("source", "local");
        var count = options.GetInt32("files", 100);
        var fileSizeBytes = options.GetInt64("file-size-kib", 64) * 1024;
        var concurrency = options.GetInt32("concurrency", 16);
        var prefix = options.GetString("prefix", $"load/index-many-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");

        var report = new ScenarioReport { Scenario = "index-many" };
        var files = await GeneratedFiles.CreateManyAsync(contentRoot, prefix, count, fileSizeBytes, cancellationToken);
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(10) };
        var api = new IcecoldApiClient(http, adminKey);
        var readyLatencies = new List<double>(count);
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(concurrency);

        var submitStopwatch = Stopwatch.StartNew();
        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var submitted = await api.IndexFileAsync(source, file.RelativePath, Path.GetFileName(file.RelativePath), cancellationToken);
                var ready = await api.PollReadyAsync(submitted, TimeSpan.FromMinutes(10), cancellationToken);
                lock (readyLatencies)
                {
                    readyLatencies.Add(ready.ReadyLatency.TotalSeconds);
                    statuses["Ready"] = statuses.GetValueOrDefault("Ready") + 1;
                }
            }
            catch
            {
                lock (readyLatencies)
                    statuses["Failed"] = statuses.GetValueOrDefault("Failed") + 1;
                throw;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        submitStopwatch.Stop();
        stopwatch.Stop();

        report.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
        report.Metrics["base_url"] = baseUrl.ToString();
        report.Metrics["content_root"] = contentRoot;
        report.Metrics["files"] = count;
        report.Metrics["file_size_bytes"] = fileSizeBytes;
        report.Metrics["concurrency"] = concurrency;
        report.Metrics["total_file_create_seconds"] = files.Sum(f => f.CreateElapsed.TotalSeconds);
        report.Metrics["index_total_seconds"] = submitStopwatch.Elapsed.TotalSeconds;
        report.Metrics["ready_per_second"] = count / Math.Max(submitStopwatch.Elapsed.TotalSeconds, 0.000001);
        report.Metrics["ready_latency_p50_seconds"] = Percentile(readyLatencies, 0.50);
        report.Metrics["ready_latency_p95_seconds"] = Percentile(readyLatencies, 0.95);
        report.Metrics["statuses"] = statuses;
        return report;
    }

    public static async Task<ScenarioReport> WebSeedThroughputAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var ready = await EnsureLiveTorrentAsync(options, cancellationToken);
        var baseUrl = options.GetUri("base-url", "http://localhost:5038");
        var adminKey = options.GetString("admin-key", Environment.GetEnvironmentVariable("ICECOLD_ADMIN_API_KEY") ?? "dev-admin-key");
        var clients = options.GetInt32("clients", 1);
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(30) };
        var api = new IcecoldApiClient(http, adminKey);

        var stopwatch = Stopwatch.StartNew();
        var measurement = await api.DownloadWebSeedAsync(ready, clients, cancellationToken);
        stopwatch.Stop();

        var report = new ScenarioReport { Scenario = "webseed-throughput", DurationSeconds = stopwatch.Elapsed.TotalSeconds };
        report.Metrics["base_url"] = baseUrl.ToString();
        report.Metrics["clients"] = clients;
        report.Metrics["info_hash"] = ready.InfoHash;
        AddTransfer(report, "webseed", measurement);
        return report;
    }

    public static async Task<ScenarioReport> PeerWireThroughputAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var ready = await EnsureLiveTorrentAsync(options, cancellationToken);
        var baseUrl = options.GetUri("base-url", "http://localhost:5038");
        var peerHost = options.GetString("peer-host", baseUrl.Host);
        var peerPort = options.GetInt32("peer-port", 6881);
        var clients = options.GetInt32("clients", 1);
        var encrypted = options.GetBoolean("encrypted", false);
        var blockBytes = options.GetInt32("block-bytes", 16 * 1024);
        var outstanding = options.GetInt32("outstanding", 256);

        var stopwatch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, clients)
            .Select(clientIndex => new PeerWireLoadClient(peerHost, peerPort, encrypted, blockBytes, outstanding)
                .DownloadAsync(ready, clientIndex, cancellationToken))
            .ToArray();
        var measurements = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var totalBytes = measurements.Sum(m => m.Bytes);
        var report = new ScenarioReport { Scenario = "peerwire-throughput", DurationSeconds = stopwatch.Elapsed.TotalSeconds };
        report.Metrics["peer_host"] = peerHost;
        report.Metrics["peer_port"] = peerPort;
        report.Metrics["clients"] = clients;
        report.Metrics["encrypted"] = encrypted;
        report.Metrics["block_bytes"] = blockBytes;
        report.Metrics["outstanding"] = outstanding;
        report.Metrics["info_hash"] = ready.InfoHash;
        report.Metrics["bytes"] = totalBytes;
        report.Metrics["mib_per_second"] = totalBytes / 1024d / 1024d / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001);
        report.Metrics["client_mib_per_second"] = measurements.Select(m => m.MiBPerSecond).ToArray();
        return report;
    }

    static async Task<ReadyTorrent> EnsureLiveTorrentAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var baseUrl = options.GetUri("base-url", "http://localhost:5038");
        var adminKey = options.GetString("admin-key", Environment.GetEnvironmentVariable("ICECOLD_ADMIN_API_KEY") ?? "dev-admin-key");
        var contentRoot = Required(options, "content-root");
        var source = options.GetString("source", "local");
        var fileSizeBytes = options.GetInt64("file-size-mib", 256) * 1024 * 1024;
        var relativePath = options.GetString("path", $"load/throughput-{fileSizeBytes}.bin");
        var displayName = options.GetString("display-name", Path.GetFileName(relativePath));

        await GeneratedFiles.EnsureAsync(contentRoot, relativePath, fileSizeBytes, cancellationToken);
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(10) };
        var api = new IcecoldApiClient(http, adminKey);
        var submitted = await api.IndexFileAsync(source, relativePath, displayName, cancellationToken);
        return await api.PollReadyAsync(submitted, TimeSpan.FromMinutes(10), cancellationToken);
    }

    static void AddTransfer(ScenarioReport report, string prefix, TransferMeasurement measurement)
    {
        report.Metrics[$"{prefix}_bytes"] = measurement.Bytes;
        report.Metrics[$"{prefix}_seconds"] = measurement.Elapsed.TotalSeconds;
        report.Metrics[$"{prefix}_mib_per_second"] = measurement.MiBPerSecond;
    }

    static string Required(CliOptions options, string key)
        => options.GetString(key) ?? throw new ArgumentException($"--{key} is required for this scenario.");

    static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Icecold.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Icecold.slnx from the load-test binary directory.");
    }
}
