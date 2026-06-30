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
        var localState = state;
        var localI = i;
        var localJ = j;
        for (var index = 0; index < byteCount; index++)
        {
            localI = unchecked((byte)(localI + 1));
            var si = localState[localI];
            localJ = unchecked((byte)(localJ + si));
            var sj = localState[localJ];
            localState[localI] = sj;
            localState[localJ] = si;
        }

        i = localI;
        j = localJ;
    }

    public void Process(Span<byte> buffer)
    {
        var localState = state;
        var localI = i;
        var localJ = j;
        for (var index = 0; index < buffer.Length; index++)
        {
            localI = unchecked((byte)(localI + 1));
            var si = localState[localI];
            localJ = unchecked((byte)(localJ + si));
            var sj = localState[localJ];
            localState[localI] = sj;
            localState[localJ] = si;
            buffer[index] ^= localState[unchecked((byte)(si + sj))];
        }

        i = localI;
        j = localJ;
    }

    public void Process(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("RC4 destination buffer is too small.", nameof(destination));

        var localState = state;
        var localI = i;
        var localJ = j;
        for (var index = 0; index < source.Length; index++)
        {
            localI = unchecked((byte)(localI + 1));
            var si = localState[localI];
            localJ = unchecked((byte)(localJ + si));
            var sj = localState[localJ];
            localState[localI] = sj;
            localState[localJ] = si;
            destination[index] = (byte)(source[index] ^ localState[unchecked((byte)(si + sj))]);
        }

        i = localI;
        j = localJ;
    }

    void Swap(int left, int right)
    {
        var value = state[left];
        state[left] = state[right];
        state[right] = value;
    }
}
