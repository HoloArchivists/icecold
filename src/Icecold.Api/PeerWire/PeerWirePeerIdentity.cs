using System.Security.Cryptography;
using System.Text;

namespace Icecold.Api.PeerWire;

public sealed class PeerWirePeerIdentity
{
    public PeerWirePeerIdentity()
    {
        var random = RandomNumberGenerator.GetBytes(12);
        var peerId = new byte[20];
        Encoding.ASCII.GetBytes("-IC0001-", peerId);
        random.CopyTo(peerId.AsSpan(8));
        PeerId = peerId;
    }

    public byte[] PeerId { get; }
}
