using System.Net;
using Icecold.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Icecold.Tests;

public sealed class PublicStatsEndpointTests
{
    [Fact]
    public async Task Stats_Endpoint_Is_Public_And_Cacheable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Icecold:Stats:CacheSeconds"] = "5",
                    ["Icecold:WebSeed:Enabled"] = "true",
                    ["Icecold:PeerWire:Enabled"] = "false"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<IcecoldDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IcecoldDbContext>>();
                services.AddDbContext<IcecoldDbContext>(options =>
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString("n")));
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/stats", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=5", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("\"torrents\"", body);
        Assert.Contains("\"tracker\"", body);
        Assert.DoesNotContain("\"locations\"", body);
        Assert.DoesNotContain("\"sources\"", body);
        Assert.DoesNotContain("\"pending\"", body);
        Assert.DoesNotContain("\"failed\"", body);
        Assert.DoesNotContain("\"peerWireAdvertisedIp\"", body);
    }

    [Fact]
    public async Task Stats_Endpoint_Returns_NotFound_When_Disabled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Icecold:Stats:Enabled"] = "false",
                    ["Icecold:WebSeed:Enabled"] = "true",
                    ["Icecold:PeerWire:Enabled"] = "false"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<IcecoldDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IcecoldDbContext>>();
                services.AddDbContext<IcecoldDbContext>(options =>
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString("n")));
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/stats", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
