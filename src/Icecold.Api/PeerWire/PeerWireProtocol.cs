using System.Text;

namespace Icecold.Api.PeerWire;

static class PeerWireProtocol
{
    public static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("BitTorrent protocol");
}
