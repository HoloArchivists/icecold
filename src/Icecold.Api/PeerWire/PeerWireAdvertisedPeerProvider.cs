using System.Net;
using Icecold.Api.Options;
using Icecold.Api.Tracker;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireAdvertisedPeerProvider(
    IOptions<IcecoldOptions> options,
    PeerWirePeerIdentity identity)
{
    public bool TryGetPeer(out PeerSnapshot peer)
    {
        var peerWire = options.Value.PeerWire;
        if (peerWire.Enabled && IPAddress.TryParse(peerWire.AdvertisedIp, out var ipAddress))
        {
            peer = new PeerSnapshot(identity.PeerId, ipAddress, peerWire.AdvertisedPort, 0);
            return true;
        }

        peer = default!;
        return false;
    }
}
