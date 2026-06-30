using Icecold.LoadTests;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return;
}

using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
var scenario = args[0].ToLowerInvariant();
var options = CliOptions.Parse(args[1..]);

ScenarioReport report = scenario switch
{
    "e2e-smoke" => await Scenarios.E2eSmokeAsync(options, cts.Token),
    "index-many" => await Scenarios.IndexManyAsync(options, cts.Token),
    "webseed-throughput" => await Scenarios.WebSeedThroughputAsync(options, cts.Token),
    "peerwire-throughput" or "peerwire-leechers" => await Scenarios.PeerWireThroughputAsync(options, cts.Token),
    _ => throw new ArgumentException($"Unknown scenario '{args[0]}'. Run with --help to see available scenarios.")
};

report.Print();
if (options.GetString("output") is { } outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    await File.WriteAllTextAsync(outputPath, report.ToJson() + Environment.NewLine, cts.Token);
}

static void PrintHelp()
{
    Console.WriteLine("""
Icecold load and E2E runner

Scenarios:
  e2e-smoke
      Starts PostgreSQL with Testcontainers, launches the API on random local ports,
      indexes a generated file, then verifies torrent metadata, tracker announce,
      webseed download, plaintext peer-wire, and MSE peer-wire.

      dotnet run -c Release --project tests/Icecold.LoadTests -- e2e-smoke --file-size-mib 64

  index-many
      Creates many files under --content-root, submits /api/index/file concurrently,
      waits for Ready, and reports ready/sec plus p50/p95 ready latency.

      dotnet run -c Release --project tests/Icecold.LoadTests -- index-many \
        --base-url http://localhost:5038 --admin-key dev-admin-key \
        --content-root src/Icecold.Api/data/files --files 1000 --file-size-kib 64 --concurrency 32

  webseed-throughput
      Ensures one generated file is indexed, then downloads it through /webseed with
      one or more concurrent clients.

      dotnet run -c Release --project tests/Icecold.LoadTests -- webseed-throughput \
        --base-url http://localhost:5038 --content-root src/Icecold.Api/data/files \
        --file-size-mib 1024 --clients 4

  peerwire-throughput
      Ensures one generated file is indexed, then downloads it through Icecold's
      peer-wire TCP server. Use --encrypted true for MSE/RC4.

      dotnet run -c Release --project tests/Icecold.LoadTests -- peerwire-throughput \
        --base-url http://localhost:5038 --content-root src/Icecold.Api/data/files \
        --peer-host 127.0.0.1 --peer-port 6881 --file-size-mib 1024 \
        --clients 1 --encrypted true --outstanding 256

Common options:
  --base-url URL            Icecold HTTP base URL. Default: http://localhost:5038
  --admin-key VALUE         Admin API key. Default: ICECOLD_ADMIN_API_KEY or dev-admin-key
  --content-root PATH       Local path mounted as the Icecold local content source.
  --source NAME             Content source name. Default: local
  --path PATH               Relative path to use for throughput fixture.
  --file-size-mib N         Throughput fixture size. Default varies by scenario.
  --clients N               Concurrent downloader count. Default: 1
  --peer-host HOST          Peer-wire host. Default: base URL host
  --peer-port PORT          Peer-wire port. Default: 6881
  --encrypted true|false    Use MSE/RC4 for peer-wire. Default: false
  --outstanding N           Pipelined peer-wire requests per client. Default: 256
  --output PATH             Also write the JSON report to a file.
""");
}
