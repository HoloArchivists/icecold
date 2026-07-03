using System.Globalization;
using System.Net;

namespace Icecold.Api.Options;

public readonly record struct IpNetworkRange(IPAddress NetworkAddress, int PrefixLength)
{
    public static bool TryParse(string text, out IpNetworkRange network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var address))
            return false;

        address = Normalize(address);
        var maxPrefixLength = address.GetAddressBytes().Length * 8;
        var prefixLength = maxPrefixLength;
        if (parts.Length == 2
            && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength)
                || prefixLength < 0
                || prefixLength > maxPrefixLength))
        {
            return false;
        }

        network = new IpNetworkRange(address, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        address = Normalize(address);
        var networkBytes = NetworkAddress.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length)
            return false;

        var fullBytes = PrefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
                return false;
        }

        var remainingBits = PrefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xff << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
