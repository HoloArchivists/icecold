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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
