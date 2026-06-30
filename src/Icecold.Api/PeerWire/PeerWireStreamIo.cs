namespace Icecold.Api.PeerWire;

static class PeerWireStreamIo
{
    public static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
        => await ReadByteAsync(stream, Timeout.InfiniteTimeSpan, cancellationToken);

    public static async Task<int> ReadByteAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var one = new byte[1];
        return await ReadExactlyOrFalseAsync(stream, one, timeout, cancellationToken) ? one[0] : -1;
    }

    public static async Task<bool> ReadExactlyOrFalseAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        => await ReadExactlyOrFalseAsync(stream, buffer, Timeout.InfiniteTimeSpan, cancellationToken);

    public static async Task<bool> ReadExactlyOrFalseAsync(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await ReadAsync(stream, buffer[read..], timeout, cancellationToken);
            if (current == 0)
                return false;

            read += current;
        }

        return true;
    }

    static async ValueTask<int> ReadAsync(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return await stream.ReadAsync(buffer, cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await stream.ReadAsync(buffer, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Peer-wire read timed out.");
        }
    }
}
