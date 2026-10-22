using Icecold.Api.Data;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Torrents;

public sealed class PublicUrlBuilder(IOptions<IcecoldOptions> options)
{
    readonly IcecoldOptions icecold = options.Value;
    readonly string publicBaseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
    readonly string webSeedPublicBaseUrl = string.IsNullOrWhiteSpace(options.Value.WebSeed.PublicBaseUrl)
        ? options.Value.PublicBaseUrl.TrimEnd('/')
        : options.Value.WebSeed.PublicBaseUrl.TrimEnd('/');

    public bool WebSeedEnabled => icecold.WebSeed.Enabled;

    public bool TrackerEnabled => icecold.Tracker.Enabled;

    public string TrackerAnnounceUrl => $"{publicBaseUrl}/announce";

    public IReadOnlyList<string> TrackerAnnounceUrls
        => [.. OwnTrackerAnnounceUrls()
            .Concat(icecold.Tracker.AdditionalAnnounceUrls)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.Ordinal)];

    IEnumerable<string> OwnTrackerAnnounceUrls()
    {
        if (TrackerEnabled)
            yield return TrackerAnnounceUrl;
    }

    // BEP 19: a single-file URL names the file itself; a multi-file URL is a directory (trailing
    // slash) to which clients append the torrent name and each file's path.
    public string BuildWebSeedUrl(string infoHashHex, string displayName, bool multiFile = false)
        => multiFile
            ? $"{webSeedPublicBaseUrl}/webseed/{infoHashHex}/"
            : $"{webSeedPublicBaseUrl}/webseed/{infoHashHex}/{Uri.EscapeDataString(displayName)}";

    public string BuildMagnet(TorrentRecord torrent)
    {
        if (string.IsNullOrWhiteSpace(torrent.InfoHashHex))
            throw new InvalidOperationException("Torrent does not have an info hash yet.");

        var webSeed = BuildWebSeedUrl(torrent.InfoHashHex, torrent.DisplayName, torrent.IsMultiFile);
        return "magnet:?xt=urn:btih:" + Uri.EscapeDataString(torrent.InfoHashHex)
            + "&dn=" + Uri.EscapeDataString(torrent.DisplayName)
            + "&xl=" + torrent.ContentLength
            + string.Concat(TrackerAnnounceUrls.Select(url => "&tr=" + Uri.EscapeDataString(url)))
            + (WebSeedEnabled ? "&ws=" + Uri.EscapeDataString(webSeed) : "");
    }
}
