using System.Net;
using Icecold.Api.Admin;
using Icecold.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Icecold.Tests;

public sealed class FolderEndpointTests
{
    [Fact]
    public async Task Folder_Indexing_Is_Not_Implemented_In_V1()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Icecold:AdminApiKey"] = "dev-admin-key"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/folder");
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Endpoints_Fail_Closed_Without_Configured_Key()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Non-Development so a local appsettings.Development.json cannot supply a key; Production refuses to start without one.
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Staging");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/folder");
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-key");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Endpoints_Reject_Wrong_Key()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Icecold:AdminApiKey"] = "dev-admin-key"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/index/folder");
        request.Headers.Add("X-Icecold-Admin-Key", "dev-admin-kez");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Production_Startup_Fails_Without_Configured_Key()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Icecold:AdminApiKey must be configured in Production.", exception.ToString());
    }

    [Fact]
    public void AdminApiKeyOptionsValidator_Only_Requires_Key_In_Production()
    {
        var options = new IcecoldOptions();

        Assert.True(new AdminApiKeyOptionsValidator(new TestHostEnvironment(Environments.Production)).Validate(null, options).Failed);
        Assert.False(new AdminApiKeyOptionsValidator(new TestHostEnvironment(Environments.Development)).Validate(null, options).Failed);
        Assert.False(new AdminApiKeyOptionsValidator(new TestHostEnvironment(Environments.Production))
            .Validate(null, new IcecoldOptions { AdminApiKey = "key" }).Failed);
    }

    sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Icecold.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
