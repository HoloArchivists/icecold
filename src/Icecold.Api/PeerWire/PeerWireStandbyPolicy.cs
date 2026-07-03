using Icecold.Api.Options;
using Icecold.Api.Tracker;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireStandbyPolicy(IOptions<IcecoldOptions> options)
{
    public bool ShouldSeed(TrackerSwarmStats externalSwarm)
    {
        var peerWire = options.Value.PeerWire;
        if (!peerWire.Enabled)
            return false;

        var standby = peerWire.Standby;
        return !standby.Enabled || externalSwarm.Seeders < standby.MinimumExternalSeeders;
    }
}
