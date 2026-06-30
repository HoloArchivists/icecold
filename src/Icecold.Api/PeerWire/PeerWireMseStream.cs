using System.Buffers;

namespace Icecold.Api.PeerWire;

sealed class PeerWireMseStream(
    Stream inner,
    PeerWireRc4 decrypt,
    PeerWireRc4 encrypt,
    byte[] initialPlaintext) : Stream
{
    int initialOffset;

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
        var copied = CopyInitial(buffer);
        if (copied > 0)
            return copied;

        var read = inner.Read(buffer);
        decrypt.Process(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var copied = CopyInitial(buffer.Span);
        if (copied > 0)
            return copied;

        var read = await inner.ReadAsync(buffer, cancellationToken);
        decrypt.Process(buffer.Span[..read]);
        return read;
    }

    int CopyInitial(Span<byte> destination)
    {
        var remaining = initialPlaintext.Length - initialOffset;
        if (remaining <= 0 || destination.Length == 0)
            return 0;

        var copied = Math.Min(remaining, destination.Length);
        initialPlaintext.AsSpan(initialOffset, copied).CopyTo(destination);
        initialOffset += copied;
        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            encrypt.Process(buffer, rented.AsSpan(0, buffer.Length));
            inner.Write(rented, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            encrypt.Process(buffer.Span, rented.AsSpan(0, buffer.Length));
            await inner.WriteAsync(rented.AsMemory(0, buffer.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
