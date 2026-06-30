using Icecold.Api.Admin;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Icecold.Api.Tracker;
using Icecold.Api.WebSeed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<AdminApiKeyHeaderOperationFilter>();
});

builder.Services.AddOptions<IcecoldOptions>()
    .Bind(builder.Configuration.GetSection(IcecoldOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<IcecoldOptions>, PeerWireOptionsValidator>();

builder.Services.AddDbContext<IcecoldDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Icecold");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("ConnectionStrings:Icecold is required.");

    options.UseNpgsql(connectionString);
});

builder.Services.AddSingleton<ContentSourceRegistry>();
builder.Services.AddSingleton<PublicUrlBuilder>();
builder.Services.AddSingleton<TorrentBuilder>();
builder.Services.AddSingleton<TorrentLocationService>();
builder.Services.AddSingleton<IIndexingQueue, ChannelIndexingQueue>();
builder.Services.AddSingleton<PeerWirePeerIdentity>();
builder.Services.AddSingleton<PeerWireAdvertisedPeerProvider>();
builder.Services.AddSingleton<PeerWireTorrentResolver>();
builder.Services.AddSingleton<PeerWireTransportNegotiator>();
builder.Services.AddSingleton<PeerWireConnectionHandler>();
builder.Services.AddSingleton<ITrackerPeerStore, InMemoryTrackerPeerStore>();
builder.Services.AddScoped<AdminApiKeyAuthorizationFilter>();
builder.Services.AddScoped<IndexFileService>();
builder.Services.AddScoped<TorrentMetadataService>();
builder.Services.AddScoped<TrackerAnnounceService>();
builder.Services.AddScoped<TrackerScrapeService>();
builder.Services.AddScoped<WebSeedService>();
builder.Services.AddHostedService<IndexingWorker>();
builder.Services.AddHostedService<PeerWireMseHashBackfillService>();
builder.Services.AddHostedService<PeerWireServer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Icecold API v1");
        options.DocumentTitle = "Icecold API";
    });
}

var icecoldOptions = app.Services.GetRequiredService<IOptions<IcecoldOptions>>().Value;
if (icecoldOptions.Database.AutoMigrate)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<IcecoldDbContext>();
    await db.Database.MigrateAsync();
}

app.MapControllers();

app.Run();

public partial class Program;
