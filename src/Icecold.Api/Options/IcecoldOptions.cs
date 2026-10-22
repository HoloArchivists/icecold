namespace Icecold.Api.Options;

public sealed class IcecoldOptions
{
    public const string SectionName = "Icecold";

    public string PublicBaseUrl { get; set; } = "http://localhost:5254";

    public string AdminApiKey { get; set; } = "";

    public DatabaseOptions Database { get; set; } = new();

    public IndexingOptions Indexing { get; set; } = new();

    public ReverseProxyOptions ReverseProxy { get; set; } = new();

    public TrackerOptions Tracker { get; set; } = new();

    public StatsOptions Stats { get; set; } = new();

    public WebSeedOptions WebSeed { get; set; } = new();

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

    // Idle workers check the database this often for jobs submitted by other instances or retries
    // whose backoff has passed. Jobs submitted to this instance wake a worker immediately.
    public int PollIntervalSeconds { get; set; } = 5;

    // A claimed job is reclaimed by another worker if its lease is not renewed within this time.
    public int LeaseDurationSeconds { get; set; } = 60;
}

public sealed class ReverseProxyOptions
{
    public bool Enabled { get; set; }

    public List<string> KnownProxies { get; set; } = [];

    public List<string> KnownNetworks { get; set; } = [];

    public int ForwardLimit { get; set; } = 1;
}

public sealed class TrackerOptions
{
    public bool Enabled { get; set; } = true;

    public bool TrackPublicPeers { get; set; } = true;

    public int AnnounceIntervalSeconds { get; set; } = 1800;

    public int MinAnnounceIntervalSeconds { get; set; } = 300;

    public int PeerTimeoutSeconds { get; set; } = 2700;

    public int MaxPeersReturned { get; set; } = 200;

    public int MaxPeersStoredPerTorrent { get; set; } = 1000;

    public int MaxPeersPerSourceIpPerTorrent { get; set; } = 8;

    public int PruneIntervalSeconds { get; set; } = 300;

    public List<string> AdditionalAnnounceUrls { get; set; } = [];

    public List<string> TrustedIpParameterSources { get; set; } = [];

    public int MaxAnnouncesPerSourceIpPerMinute { get; set; } = 120;
}

public sealed class StatsOptions
{
    public bool Enabled { get; set; } = true;

    public int CacheSeconds { get; set; } = 5;
}

public sealed class WebSeedOptions
{
    public bool Enabled { get; set; } = true;

    public string? PublicBaseUrl { get; set; }
}

public sealed class PeerWireOptions
{
    public bool Enabled { get; set; }

    public PeerWireSuperSeedOptions SuperSeed { get; set; } = new();

    public PeerWireStandbyOptions Standby { get; set; } = new();

    public string BindAddress { get; set; } = "0.0.0.0";

    public int ListenPort { get; set; } = 6881;

    public string AdvertisedIp { get; set; } = "";

    public int AdvertisedPort { get; set; } = 6881;

    public int MaxBlockLength { get; set; } = 16 * 1024;

    public int MaxOutstandingRequests { get; set; } = 8192;

    public int MaxConnections { get; set; } = 128;

    public int MaxConnectionsPerIp { get; set; } = 8;

    public int HandshakeTimeoutSeconds { get; set; } = 15;

    public int IdleTimeoutSeconds { get; set; } = 120;
}

public sealed class PeerWireSuperSeedOptions
{
    public bool Enabled { get; set; }

    public int MinimumPeerCount { get; set; }

    public int MaxPiecesPerPeer { get; set; } = 1;
}

public sealed class PeerWireStandbyOptions
{
    public bool Enabled { get; set; }

    public int MinimumExternalSeeders { get; set; } = 2;

    public int MinimumExternalSeederAgeSeconds { get; set; } = 300;
}

public sealed class ContentSourceOptions
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "local";

    public string RootPath { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    // HTTP sources: extra request headers, e.g. Authorization for a private origin.
    public Dictionary<string, string> Headers { get; set; } = [];

    // HTTP sources: limit for connecting and receiving response headers, and for each body read.
    public int RequestTimeoutSeconds { get; set; } = 30;

    public int MaxConnections { get; set; } = 32;

    // HTTP sources: how long HEAD results are reused. Reads stay pinned to the cached version, so
    // a change inside this window is still caught by the origin's 412 or the response validators.
    public int MetadataCacheSeconds { get; set; } = 5;

    // HTTP sources: window fetched per range request when peer-wire reads 16 KiB blocks.
    public int ReadAheadBytes { get; set; } = 1024 * 1024;

    // HTTP sources: reconnects allowed without progress before a body read fails.
    public int MaxResumeAttempts { get; set; } = 3;
}
