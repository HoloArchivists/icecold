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

    public string TrackerAnnounceUrl => $"{publicBaseUrl}/announce";

    public string BuildWebSeedUrl(string infoHashHex, string displayName)
        => $"{webSeedPublicBaseUrl}/webseed/{infoHashHex}/{Uri.EscapeDataString(displayName)}";

    public string BuildMagnet(TorrentRecord torrent)
    {
        if (string.IsNullOrWhiteSpace(torrent.InfoHashHex))
            throw new InvalidOperationException("Torrent does not have an info hash yet.");

        var webSeed = BuildWebSeedUrl(torrent.InfoHashHex, torrent.DisplayName);
        return "magnet:?xt=urn:btih:" + Uri.EscapeDataString(torrent.InfoHashHex)
            + "&dn=" + Uri.EscapeDataString(torrent.DisplayName)
            + "&xl=" + torrent.ContentLength
            + "&tr=" + Uri.EscapeDataString(TrackerAnnounceUrl)
            + (WebSeedEnabled ? "&ws=" + Uri.EscapeDataString(webSeed) : "");
    }
}
