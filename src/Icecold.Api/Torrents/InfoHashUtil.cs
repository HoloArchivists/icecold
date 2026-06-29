using System.Security.Cryptography;

namespace Icecold.Api.Torrents;

public static class InfoHashUtil
{
    public static string Sha1Hex(ReadOnlySpan<byte> bytes)
        => ToHex(SHA1.HashData(bytes));

    public static string ToHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    public static bool IsHexInfoHash(string value)
        => value.Length == 40 && value.All(Uri.IsHexDigit);
}
