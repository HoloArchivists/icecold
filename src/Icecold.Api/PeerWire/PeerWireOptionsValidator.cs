using System.Net;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var peerWire = options.PeerWire;
        if (!peerWire.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (!IPAddress.TryParse(peerWire.BindAddress, out _))
            failures.Add("Icecold:PeerWire:BindAddress must be a valid IP address.");

        if (peerWire.ListenPort is < 1 or > 65535)
            failures.Add("Icecold:PeerWire:ListenPort must be between 1 and 65535.");

        if (!IPAddress.TryParse(peerWire.AdvertisedIp, out _))
            failures.Add("Icecold:PeerWire:AdvertisedIp must be a valid IP address.");

        if (peerWire.AdvertisedPort is < 1 or > 65535)
            failures.Add("Icecold:PeerWire:AdvertisedPort must be between 1 and 65535.");

        if (peerWire.MaxBlockLength is < 1 or > (1024 * 1024))
            failures.Add("Icecold:PeerWire:MaxBlockLength must be between 1 and 1048576 bytes.");

        if (peerWire.MaxOutstandingRequests is < 1 or > 100000)
            failures.Add("Icecold:PeerWire:MaxOutstandingRequests must be between 1 and 100000.");

        if (peerWire.MaxConnections < 1)
            failures.Add("Icecold:PeerWire:MaxConnections must be at least 1.");

        if (peerWire.HandshakeTimeoutSeconds is < 1 or > 300)
            failures.Add("Icecold:PeerWire:HandshakeTimeoutSeconds must be between 1 and 300.");

        if (peerWire.IdleTimeoutSeconds is < 1 or > 3600)
            failures.Add("Icecold:PeerWire:IdleTimeoutSeconds must be between 1 and 3600.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
