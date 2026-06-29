namespace Icecold.Api.Options;

public sealed class IcecoldOptions
{
    public const string SectionName = "Icecold";

    public string PublicBaseUrl { get; set; } = "http://localhost:5254";

    public string AdminApiKey { get; set; } = "";

    public DatabaseOptions Database { get; set; } = new();

    public IndexingOptions Indexing { get; set; } = new();

    public TrackerOptions Tracker { get; set; } = new();

    public PeerWireOptions PeerWire { get; set; } = new();

    public List<ContentSourceOptions> ContentSources { get; set; } = [];
}

public sealed class DatabaseOptions
{
    public bool AutoMigrate { get; set; }
}

public sealed class IndexingOptions
{
    public int MaxConcurrency { get; set; } = 1;
}

public sealed class TrackerOptions
{
    public int AnnounceIntervalSeconds { get; set; } = 1800;

    public int MinAnnounceIntervalSeconds { get; set; } = 300;

    public int PeerTimeoutSeconds { get; set; } = 2700;

    public int MaxPeersReturned { get; set; } = 200;
}

public sealed class PeerWireOptions
{
    public bool Enabled { get; set; }

    public string BindAddress { get; set; } = "0.0.0.0";

    public int ListenPort { get; set; } = 6881;

    public string AdvertisedIp { get; set; } = "";

    public int AdvertisedPort { get; set; } = 6881;

    public int MaxBlockLength { get; set; } = 16 * 1024;

    public int MaxConnections { get; set; } = 128;
}

public sealed class ContentSourceOptions
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "local";

    public string RootPath { get; set; } = "";

    public string BaseUrl { get; set; } = "";
}
