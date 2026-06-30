namespace Icecold.Api.PeerWire;

static class PeerWireStreamIo
{
    public static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        return await ReadExactlyOrFalseAsync(stream, one, cancellationToken) ? one[0] : -1;
    }

    public static async Task<bool> ReadExactlyOrFalseAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0)
                return false;

            read += current;
        }

        return true;
    }
}
