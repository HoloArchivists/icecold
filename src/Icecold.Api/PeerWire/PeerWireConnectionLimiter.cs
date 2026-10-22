using System.Net;
using System.Net.Sockets;

namespace Icecold.Api.PeerWire;

// Caps total and per-remote-IP peer-wire connections so one host cannot take every slot.
sealed class PeerWireConnectionLimiter(int maxConnections, int maxConnectionsPerIp)
{
    readonly object gate = new();
    readonly Dictionary<IPAddress, int> connectionsByIp = [];
    int totalConnections;

    public bool TryAcquire(IPAddress address)
    {
        address = Normalize(address);
        lock (gate)
        {
            if (totalConnections >= maxConnections)
                return false;

            connectionsByIp.TryGetValue(address, out var current);
            if (current >= maxConnectionsPerIp)
                return false;

            connectionsByIp[address] = current + 1;
            totalConnections++;
            return true;
        }
    }

    public void Release(IPAddress address)
    {
        address = Normalize(address);
        lock (gate)
        {
            if (!connectionsByIp.TryGetValue(address, out var current))
                return;

            if (current <= 1)
                connectionsByIp.Remove(address);
            else
                connectionsByIp[address] = current - 1;

            totalConnections--;
        }
    }

    // IPv6 hosts are usually handed a whole /64, so count the prefix rather than each address.
    internal static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address;

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return new IPAddress(bytes);
    }
}
