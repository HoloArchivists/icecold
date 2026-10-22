using System.Diagnostics;

namespace Icecold.Api.PeerWire;

// One absolute deadline shared by every handshake read and write (MSE negotiation plus the
// BitTorrent handshake), so a peer dribbling one byte per read timeout cannot hold a slot.
sealed class PeerWireDeadline
{
    readonly long startTimestamp = Stopwatch.GetTimestamp();
    readonly TimeSpan timeout;

    PeerWireDeadline(TimeSpan timeout)
    {
        this.timeout = timeout;
    }

    public static PeerWireDeadline After(TimeSpan timeout)
        => new(timeout);

    public TimeSpan Remaining
    {
        get
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Peer-wire handshake deadline exceeded.");

            return remaining;
        }
    }
}
