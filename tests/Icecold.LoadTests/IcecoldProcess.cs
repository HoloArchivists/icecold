using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;

namespace Icecold.LoadTests;

sealed class IcecoldProcess : IAsyncDisposable
{
    readonly Process process;
    readonly PostgreSqlContainer? postgres;
    readonly ConcurrentQueue<string> recentOutput = new();

    IcecoldProcess(Process process, PostgreSqlContainer? postgres, Uri baseUrl, int peerWirePort, string contentRoot)
    {
        this.process = process;
        this.postgres = postgres;
        BaseUrl = baseUrl;
        PeerWirePort = peerWirePort;
        ContentRoot = contentRoot;
    }

    public Uri BaseUrl { get; }

    public int PeerWirePort { get; }

    public string ContentRoot { get; }

    public static async Task<IcecoldProcess> StartWithPostgresAsync(
        string repoRoot,
        string contentRoot,
        int indexingConcurrency,
        CancellationToken cancellationToken)
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("icecold")
            .WithUsername("icecold")
            .WithPassword("icecold")
            .Build();

        await postgres.StartAsync(cancellationToken);
        IcecoldProcess? started = null;
        try
        {
            var httpPort = GetFreeTcpPort();
            var peerWirePort = GetFreeTcpPort();
            var baseUrl = new Uri($"http://127.0.0.1:{httpPort}");
            var apiProject = Path.Combine(repoRoot, "src", "Icecold.Api", "Icecold.Api.csproj");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --no-launch-profile --configuration Release --project \"{apiProject}\"",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            var environment = process.StartInfo.Environment;
            environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            environment["ASPNETCORE_URLS"] = baseUrl.ToString();
            environment["ConnectionStrings__Icecold"] = postgres.GetConnectionString();
            environment["Icecold__AdminApiKey"] = "load-test-admin";
            environment["Icecold__PublicBaseUrl"] = baseUrl.ToString().TrimEnd('/');
            environment["Icecold__Database__AutoMigrate"] = "true";
            environment["Icecold__Indexing__MaxConcurrency"] = indexingConcurrency.ToString();
            environment["Icecold__ContentSources__0__Name"] = "local";
            environment["Icecold__ContentSources__0__Type"] = "local";
            environment["Icecold__ContentSources__0__RootPath"] = contentRoot;
            environment["Icecold__PeerWire__Enabled"] = "true";
            environment["Icecold__PeerWire__BindAddress"] = "127.0.0.1";
            environment["Icecold__PeerWire__ListenPort"] = peerWirePort.ToString();
            environment["Icecold__PeerWire__AdvertisedIp"] = "127.0.0.1";
            environment["Icecold__PeerWire__AdvertisedPort"] = peerWirePort.ToString();
            environment["Icecold__PeerWire__MaxConnections"] = "512";

            if (!process.Start())
                throw new InvalidOperationException("Failed to start Icecold API process.");

            started = new IcecoldProcess(process, postgres, baseUrl, peerWirePort, contentRoot);
            started.StartOutputPump(process.StandardOutput);
            started.StartOutputPump(process.StandardError);

            await started.WaitForHealthAsync(cancellationToken);
            return started;
        }
        catch
        {
            if (started is not null)
                await started.DisposeAsync();
            else
                await postgres.DisposeAsync();

            throw;
        }
    }

    async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Icecold API exited during startup.{Environment.NewLine}{GetRecentOutput()}");

            try
            {
                using var response = await http.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Icecold API did not become healthy within 60 seconds.{Environment.NewLine}{GetRecentOutput()}");
    }

    void StartOutputPump(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                recentOutput.Enqueue(line);
                while (recentOutput.Count > 80 && recentOutput.TryDequeue(out _))
                {
                }
            }
        });
    }

    string GetRecentOutput()
        => string.Join(Environment.NewLine, recentOutput);

    static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
        if (postgres is not null)
            await postgres.DisposeAsync();
    }
}
