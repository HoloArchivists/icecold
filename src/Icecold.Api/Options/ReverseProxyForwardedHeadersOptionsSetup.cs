using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Options;

public sealed class ReverseProxyForwardedHeadersOptionsSetup(IOptions<IcecoldOptions> options) : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions forwardedHeaders)
    {
        var reverseProxy = options.Value.ReverseProxy;

        forwardedHeaders.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwardedHeaders.ForwardLimit = reverseProxy.ForwardLimit;
        forwardedHeaders.KnownProxies.Clear();
        forwardedHeaders.KnownIPNetworks.Clear();

        foreach (var proxy in reverseProxy.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var ipAddress))
                forwardedHeaders.KnownProxies.Add(IpNetworkRange.Normalize(ipAddress));
        }

        foreach (var networkText in reverseProxy.KnownNetworks)
        {
            if (IpNetworkRange.TryParse(networkText, out var network))
                forwardedHeaders.KnownIPNetworks.Add(new System.Net.IPNetwork(network.NetworkAddress, network.PrefixLength));
        }
    }
}
