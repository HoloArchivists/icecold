namespace Icecold.Api.PeerWire;

public sealed class PeerWireTransportNegotiator(ILogger<PeerWireTransportNegotiator> logger)
{
    const int PlaintextHandshakeLengthByte = 19;

    public async Task<Stream?> NegotiateAsync(Stream stream, CancellationToken cancellationToken)
    {
        var firstByte = await PeerWireStreamIo.ReadByteAsync(stream, cancellationToken);
        if (firstByte < 0)
            return null;

        if (firstByte == PlaintextHandshakeLengthByte)
            return new PeerWirePrefixedStream(stream, [(byte)firstByte]);

        logger.LogDebug(
            "Peer-wire connection started with unsupported transport byte {FirstByte}.",
            firstByte);
        return null;
    }
}
