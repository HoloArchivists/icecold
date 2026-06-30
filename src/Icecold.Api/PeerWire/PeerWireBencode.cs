using System.Globalization;
using System.Text;

namespace Icecold.Api.PeerWire;

static class PeerWireBencode
{
    public const int MaxNestingDepth = 32;

    public static bool TryExtractTorrentInfo(ReadOnlySpan<byte> torrentBytes, out byte[] infoBytes)
    {
        infoBytes = [];
        var index = 0;
        if (torrentBytes.Length == 0 || torrentBytes[index++] != (byte)'d')
            return false;

        while (index < torrentBytes.Length && torrentBytes[index] != (byte)'e')
        {
            if (!TryReadBytes(torrentBytes, ref index, out var key))
                return false;

            var valueStart = index;
            if (!TrySkipValue(torrentBytes, ref index, depth: 1))
                return false;

            if (key.SequenceEqual("info"u8))
            {
                infoBytes = torrentBytes[valueStart..index].ToArray();
                return true;
            }
        }

        return false;
    }

    public static bool TryDecodeDictionary(ReadOnlySpan<byte> bytes, out Dictionary<string, object> dictionary)
    {
        dictionary = [];
        var index = 0;
        if (!TryReadDictionary(bytes, ref index, depth: 1, out dictionary))
            return false;

        return index == bytes.Length;
    }

    public static bool TryGetInteger(Dictionary<string, object> dictionary, string key, out long value)
    {
        if (dictionary.TryGetValue(key, out var parsed) && parsed is long number)
        {
            value = number;
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryGetDictionary(Dictionary<string, object> dictionary, string key, out Dictionary<string, object> value)
    {
        if (dictionary.TryGetValue(key, out var parsed) && parsed is Dictionary<string, object> nested)
        {
            value = nested;
            return true;
        }

        value = [];
        return false;
    }

    static bool TryReadValue(ReadOnlySpan<byte> bytes, ref int index, int depth, out object value)
    {
        value = default!;
        if (index >= bytes.Length)
            return false;

        if (bytes[index] == (byte)'d')
        {
            if (!TryReadDictionary(bytes, ref index, depth + 1, out var dictionary))
                return false;

            value = dictionary;
            return true;
        }

        if (bytes[index] == (byte)'l')
        {
            if (!TryReadList(bytes, ref index, depth + 1, out var list))
                return false;

            value = list;
            return true;
        }

        return bytes[index] switch
        {
            (byte)'i' => TryReadInteger(bytes, ref index, out value),
            >= (byte)'0' and <= (byte)'9' => TryReadString(bytes, ref index, out value),
            _ => false
        };
    }

    static bool TryReadDictionary(
        ReadOnlySpan<byte> bytes,
        ref int index,
        int depth,
        out Dictionary<string, object> dictionary)
    {
        dictionary = [];
        if (depth > MaxNestingDepth)
            return false;

        if (index >= bytes.Length || bytes[index++] != (byte)'d')
            return false;

        while (index < bytes.Length && bytes[index] != (byte)'e')
        {
            if (!TryReadBytes(bytes, ref index, out var keyBytes))
                return false;
            if (!TryReadValue(bytes, ref index, depth, out var value))
                return false;

            dictionary[Encoding.UTF8.GetString(keyBytes)] = value;
        }

        if (index >= bytes.Length || bytes[index] != (byte)'e')
            return false;

        index++;
        return true;
    }

    static bool TryReadList(
        ReadOnlySpan<byte> bytes,
        ref int index,
        int depth,
        out List<object> list)
    {
        list = [];
        if (depth > MaxNestingDepth)
            return false;

        if (index >= bytes.Length || bytes[index++] != (byte)'l')
            return false;

        while (index < bytes.Length && bytes[index] != (byte)'e')
        {
            if (!TryReadValue(bytes, ref index, depth, out var value))
                return false;

            list.Add(value);
        }

        if (index >= bytes.Length || bytes[index] != (byte)'e')
            return false;

        index++;
        return true;
    }

    static bool TryReadInteger(ReadOnlySpan<byte> bytes, ref int index, out object value)
    {
        value = default!;
        if (index >= bytes.Length || bytes[index++] != (byte)'i')
            return false;

        var start = index;
        while (index < bytes.Length && bytes[index] != (byte)'e')
            index++;

        if (index >= bytes.Length)
            return false;

        if (!long.TryParse(bytes[start..index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            return false;

        index++;
        value = number;
        return true;
    }

    static bool TryReadString(ReadOnlySpan<byte> bytes, ref int index, out object value)
    {
        value = default!;
        if (!TryReadBytes(bytes, ref index, out var parsed))
            return false;

        value = Encoding.UTF8.GetString(parsed);
        return true;
    }

    static bool TryReadBytes(ReadOnlySpan<byte> bytes, ref int index, out ReadOnlySpan<byte> value)
    {
        value = [];
        var lengthStart = index;
        while (index < bytes.Length && bytes[index] != (byte)':')
        {
            if (bytes[index] is < (byte)'0' or > (byte)'9')
                return false;
            index++;
        }

        if (index >= bytes.Length || index == lengthStart)
            return false;

        if (!int.TryParse(bytes[lengthStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            return false;

        index++;
        if (length < 0 || index + length > bytes.Length)
            return false;

        value = bytes[index..(index + length)];
        index += length;
        return true;
    }

    static bool TrySkipValue(ReadOnlySpan<byte> bytes, ref int index, int depth)
    {
        if (index >= bytes.Length)
            return false;

        if (depth > MaxNestingDepth)
            return false;

        switch (bytes[index])
        {
            case (byte)'i':
                index++;
                while (index < bytes.Length && bytes[index] != (byte)'e')
                    index++;
                if (index >= bytes.Length)
                    return false;
                index++;
                return true;
            case (byte)'l':
                index++;
                while (index < bytes.Length && bytes[index] != (byte)'e')
                {
                    if (!TrySkipValue(bytes, ref index, depth + 1))
                        return false;
                }
                if (index >= bytes.Length)
                    return false;
                index++;
                return true;
            case (byte)'d':
                index++;
                while (index < bytes.Length && bytes[index] != (byte)'e')
                {
                    if (!TryReadBytes(bytes, ref index, out _))
                        return false;
                    if (!TrySkipValue(bytes, ref index, depth + 1))
                        return false;
                }
                if (index >= bytes.Length)
                    return false;
                index++;
                return true;
            case >= (byte)'0' and <= (byte)'9':
                return TryReadBytes(bytes, ref index, out _);
            default:
                return false;
        }
    }
}
