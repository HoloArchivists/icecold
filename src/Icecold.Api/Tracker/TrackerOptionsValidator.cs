using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Tracker;

public sealed class TrackerOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var tracker = options.Tracker;
        var failures = new List<string>();

        if (tracker.AnnounceIntervalSeconds < 1)
            failures.Add("Icecold:Tracker:AnnounceIntervalSeconds must be at least 1.");

        if (tracker.MinAnnounceIntervalSeconds < 1)
            failures.Add("Icecold:Tracker:MinAnnounceIntervalSeconds must be at least 1.");

        if (tracker.PeerTimeoutSeconds < 1)
            failures.Add("Icecold:Tracker:PeerTimeoutSeconds must be at least 1.");

        if (tracker.MaxPeersReturned < 0)
            failures.Add("Icecold:Tracker:MaxPeersReturned must be at least 0.");

        if (tracker.MaxPeersStoredPerTorrent < 1)
            failures.Add("Icecold:Tracker:MaxPeersStoredPerTorrent must be at least 1.");

        if (tracker.PruneIntervalSeconds < 1)
            failures.Add("Icecold:Tracker:PruneIntervalSeconds must be at least 1.");

        foreach (var url in tracker.AdditionalAnnounceUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                failures.Add("Icecold:Tracker:AdditionalAnnounceUrls entries must not be blank.");
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !(uri.Scheme is "http" or "https" or "udp"))
            {
                failures.Add("Icecold:Tracker:AdditionalAnnounceUrls entries must be absolute http, https, or udp tracker URLs.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
