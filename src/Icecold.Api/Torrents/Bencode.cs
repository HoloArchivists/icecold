using System.Buffers;
using System.Globalization;
using System.Text;

namespace Icecold.Api.Torrents;

public static class Bencode
{
    public static byte[] Encode(object value)
    {
        using var stream = new MemoryStream();
        WriteValue(stream, value);
        return stream.ToArray();
    }

    static void WriteValue(Stream stream, object value)
    {
        switch (value)
        {
            case string text:
                WriteBytes(stream, Encoding.UTF8.GetBytes(text));
                break;
            case byte[] bytes:
                WriteBytes(stream, bytes);
                break;
            case int number:
                WriteInteger(stream, number);
                break;
            case long number:
                WriteInteger(stream, number);
                break;
            case IReadOnlyDictionary<string, object> dictionary:
                WriteDictionary(stream, dictionary);
                break;
            case IReadOnlyList<object> list:
                WriteList(stream, list);
                break;
            default:
                throw new InvalidOperationException($"Unsupported bencode value '{value.GetType()}'.");
        }
    }

    static void WriteDictionary(Stream stream, IReadOnlyDictionary<string, object> dictionary)
    {
        stream.WriteByte((byte)'d');
        foreach (var (key, value) in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteBytes(stream, Encoding.UTF8.GetBytes(key));
            WriteValue(stream, value);
        }
        stream.WriteByte((byte)'e');
    }

    static void WriteList(Stream stream, IReadOnlyList<object> list)
    {
        stream.WriteByte((byte)'l');
        foreach (var value in list)
            WriteValue(stream, value);
        stream.WriteByte((byte)'e');
    }

    static void WriteInteger(Stream stream, long value)
    {
        stream.WriteByte((byte)'i');
        var text = value.ToString(CultureInfo.InvariantCulture);
        stream.Write(Encoding.ASCII.GetBytes(text));
        stream.WriteByte((byte)'e');
    }

    static void WriteBytes(Stream stream, byte[] bytes)
    {
        var length = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture));
        stream.Write(length);
        stream.WriteByte((byte)':');
        stream.Write(bytes);
    }
}
