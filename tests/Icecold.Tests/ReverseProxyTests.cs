using System.Net;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Icecold.Tests;

public sealed class ReverseProxyTests
{
    [Fact]
    public async Task Announce_Uses_XForwardedFor_From_Trusted_Proxy()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        await using var factory = CreateFactory(infoHash, reverseProxyEnabled: true, knownProxies: ["203.0.113.10"]);
        using var client = factory.CreateClient();

        using var first = await AnnounceAsync(client, infoHash, peerByte: 1, forwardedFor: "198.51.100.25");
        first.EnsureSuccessStatusCode();
        using var second = await AnnounceAsync(client, infoHash, peerByte: 2, forwardedFor: "198.51.100.26");
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Contains("198.51.100.25", body);
    }

    [Fact]
    public async Task Announce_Ignores_XForwardedFor_From_Untrusted_Proxy()
    {
        var infoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        await using var factory = CreateFactory(infoHash, reverseProxyEnabled: true, knownProxies: ["10.10.10.10"]);
        using var client = factory.CreateClient();

        using var first = await AnnounceAsync(client, infoHash, peerByte: 1, forwardedFor: "198.51.100.25");
        first.EnsureSuccessStatusCode();
        using var second = await AnnounceAsync(client, infoHash, peerByte: 2, forwardedFor: "198.51.100.26");
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.DoesNotContain("198.51.100.25", body);
    }

    [Fact]
    public void ReverseProxyOptionsValidator_Rejects_Invalid_Configuration()
    {
        var result = new ReverseProxyOptionsValidator().Validate(null, new IcecoldOptions
        {
            ReverseProxy = new ReverseProxyOptions
            {
                Enabled = true,
                ForwardLimit = 0,
                KnownProxies = ["", "not-an-ip"],
                KnownNetworks = ["", "not-a-network"]
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:ReverseProxy:ForwardLimit must be between 1 and 10.", result.Failures);
        Assert.Contains("Icecold:ReverseProxy:KnownProxies entries must not be blank.", result.Failures);
        Assert.Contains("Icecold:ReverseProxy:KnownProxies entries must be IP addresses.", result.Failures);
        Assert.Contains("Icecold:ReverseProxy:KnownNetworks entries must not be blank.", result.Failures);
        Assert.Contains("Icecold:ReverseProxy:KnownNetworks entries must be IP addresses or CIDR ranges.", result.Failures);
    }

    [Fact]
    public void ReverseProxyOptionsValidator_Requires_Trusted_Source_When_Enabled()
    {
        var result = new ReverseProxyOptionsValidator().Validate(null, new IcecoldOptions
        {
            ReverseProxy = new ReverseProxyOptions
            {
                Enabled = true
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:ReverseProxy requires at least one KnownProxies or KnownNetworks entry when enabled.", result.Failures);
    }

    static WebApplicationFactory<Program> CreateFactory(byte[] infoHash, bool reverseProxyEnabled, string[] knownProxies)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var databaseName = Guid.NewGuid().ToString("n");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Icecold:ReverseProxy:Enabled"] = reverseProxyEnabled.ToString(),
                    ["Icecold:Tracker:Enabled"] = "true",
                    ["Icecold:WebSeed:Enabled"] = "true",
                    ["Icecold:PeerWire:Enabled"] = "false"
                };

                for (var i = 0; i < knownProxies.Length; i++)
                    values[$"Icecold:ReverseProxy:KnownProxies:{i}"] = knownProxies[i];

                configuration.AddInMemoryCollection(values);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IStartupFilter>(_ => new RemoteIpStartupFilter(IPAddress.Parse("203.0.113.10")));
                services.RemoveAll<DbContextOptions<IcecoldDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IcecoldDbContext>>();
                services.AddDbContext<IcecoldDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName));

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
                AddReadyTorrent(db, infoHash);
            });
        });

    static void AddReadyTorrent(IcecoldDbContext db, byte[] infoHash)
    {
        var now = DateTimeOffset.UtcNow;
        db.Torrents.Add(new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = "payload.bin",
            DisplayName = "payload.bin",
            ContentLength = 100,
            ContentVersion = "v1",
            Status = TorrentStatus.Ready,
            InfoHashHex = InfoHashUtil.ToHex(infoHash),
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        db.SaveChanges();
    }

    static async Task<HttpResponseMessage> AnnounceAsync(HttpClient client, byte[] infoHash, byte peerByte, string forwardedFor)
    {
        var peerId = Enumerable.Repeat(peerByte, 20).ToArray();
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/announce?info_hash=" + PercentEncode(infoHash)
            + "&peer_id=" + PercentEncode(peerId)
            + "&port=" + (6880 + peerByte)
            + "&uploaded=0&downloaded=0&left=0");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return await client.SendAsync(request, CancellationToken.None);
    }

    static string PercentEncode(IEnumerable<byte> bytes)
        => string.Concat(bytes.Select(b => "%" + b.ToString("x2")));

    sealed class RemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIpAddress;
                    return nextMiddleware();
                });
                next(app);
            };
    }
}
