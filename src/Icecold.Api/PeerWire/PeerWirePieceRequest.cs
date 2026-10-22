namespace Icecold.Api.PeerWire;

public sealed record PeerWirePieceRequest(int PieceIndex, int Begin, int Length, long Offset)
{
    public static bool TryCreate(
        PeerWireTorrentContext torrent,
        int pieceIndex,
        int begin,
        int length,
        int maxBlockLength,
        out PeerWirePieceRequest request)
    {
        request = default!;

        if (pieceIndex < 0 || pieceIndex >= torrent.PieceCount)
            return false;
        if (begin < 0)
            return false;
        if (length < 1 || length > maxBlockLength)
            return false;

        // The block must stay inside its own piece; otherwise begin could address another
        // piece's data and sidestep super-seeding's per-piece advertisement.
        var pieceStart = (long)pieceIndex * torrent.PieceLength;
        var actualPieceLength = Math.Min(torrent.PieceLength, torrent.ContentLength - pieceStart);
        if ((long)begin + length > actualPieceLength)
            return false;

        var offset = pieceStart + begin;
        if (offset < 0 || offset >= torrent.ContentLength)
            return false;

        var endExclusive = offset + length;
        if (endExclusive < offset || endExclusive > torrent.ContentLength)
            return false;

        request = new PeerWirePieceRequest(pieceIndex, begin, length, offset);
        return true;
    }
}
