namespace Icecold.Api.PeerWire;

public sealed class PeerWireRc4
{
    readonly byte[] state = new byte[256];
    byte i;
    byte j;

    public PeerWireRc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));

        for (var index = 0; index < state.Length; index++)
            state[index] = (byte)index;

        byte keyIndex = 0;
        for (var index = 0; index < state.Length; index++)
        {
            j = unchecked((byte)(j + state[index] + key[keyIndex]));
            Swap(index, j);
            keyIndex = unchecked((byte)((keyIndex + 1) % key.Length));
        }

        i = 0;
        j = 0;
    }

    public void Discard(int byteCount)
    {
        for (var index = 0; index < byteCount; index++)
            Next();
    }

    public void Process(Span<byte> buffer)
    {
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] ^= Next();
    }

    public void Process(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("RC4 destination buffer is too small.", nameof(destination));

        for (var index = 0; index < source.Length; index++)
            destination[index] = (byte)(source[index] ^ Next());
    }

    byte Next()
    {
        i = unchecked((byte)(i + 1));
        j = unchecked((byte)(j + state[i]));
        Swap(i, j);
        return state[unchecked((byte)(state[i] + state[j]))];
    }

    void Swap(int left, int right)
        => (state[left], state[right]) = (state[right], state[left]);
}
