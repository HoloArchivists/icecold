namespace Icecold.Api.PeerWire;

sealed class PeerWirePrefixedStream(Stream inner, byte[] prefix) : Stream
{
    int prefixOffset;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
        => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var copied = CopyPrefix(buffer);
        return copied == buffer.Length ? copied : copied + inner.Read(buffer[copied..]);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var copied = CopyPrefix(buffer.Span);
        return copied == buffer.Length
            ? copied
            : copied + await inner.ReadAsync(buffer[copied..], cancellationToken);
    }

    int CopyPrefix(Span<byte> destination)
    {
        var remaining = prefix.Length - prefixOffset;
        if (remaining <= 0 || destination.Length == 0)
            return 0;

        var copied = Math.Min(remaining, destination.Length);
        prefix.AsSpan(prefixOffset, copied).CopyTo(destination);
        prefixOffset += copied;
        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer)
        => inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);
}
