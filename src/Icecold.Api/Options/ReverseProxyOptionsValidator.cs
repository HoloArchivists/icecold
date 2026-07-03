using System.Net;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Options;

public sealed class ReverseProxyOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var reverseProxy = options.ReverseProxy;
        var failures = new List<string>();

        if (reverseProxy.ForwardLimit is < 1 or > 10)
            failures.Add("Icecold:ReverseProxy:ForwardLimit must be between 1 and 10.");

        if (reverseProxy.Enabled
            && reverseProxy.KnownProxies.Count == 0
            && reverseProxy.KnownNetworks.Count == 0)
        {
            failures.Add("Icecold:ReverseProxy requires at least one KnownProxies or KnownNetworks entry when enabled.");
        }

        foreach (var proxy in reverseProxy.KnownProxies)
        {
            if (string.IsNullOrWhiteSpace(proxy))
            {
                failures.Add("Icecold:ReverseProxy:KnownProxies entries must not be blank.");
                continue;
            }

            if (!IPAddress.TryParse(proxy, out _))
                failures.Add("Icecold:ReverseProxy:KnownProxies entries must be IP addresses.");
        }

        foreach (var network in reverseProxy.KnownNetworks)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                failures.Add("Icecold:ReverseProxy:KnownNetworks entries must not be blank.");
                continue;
            }

            if (!IpNetworkRange.TryParse(network, out _))
                failures.Add("Icecold:ReverseProxy:KnownNetworks entries must be IP addresses or CIDR ranges.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
