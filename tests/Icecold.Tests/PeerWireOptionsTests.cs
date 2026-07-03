using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class PeerWireOptionsTests
{
    [Fact]
    public void Validate_Fails_When_Enabled_With_Invalid_Advertised_Ip()
    {
        var result = new PeerWireOptionsValidator().Validate(null, new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                BindAddress = "0.0.0.0",
                ListenPort = 6881,
                AdvertisedIp = "example.test",
                AdvertisedPort = 6881,
                MaxBlockLength = 16 * 1024,
                MaxOutstandingRequests = 8192,
                MaxConnections = 128
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:PeerWire:AdvertisedIp must be a valid IP address.", result.Failures);
    }

    [Fact]
    public void Validate_Fails_When_Enabled_With_Invalid_Max_Outstanding_Requests()
    {
        var result = new PeerWireOptionsValidator().Validate(null, new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                BindAddress = "0.0.0.0",
                ListenPort = 6881,
                AdvertisedIp = "192.168.0.150",
                AdvertisedPort = 6881,
                MaxBlockLength = 16 * 1024,
                MaxOutstandingRequests = 0,
                MaxConnections = 128
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:PeerWire:MaxOutstandingRequests must be between 1 and 100000.", result.Failures);
    }

    [Fact]
    public void Validate_Fails_When_Enabled_With_Invalid_Timeouts()
    {
        var result = new PeerWireOptionsValidator().Validate(null, new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                BindAddress = "0.0.0.0",
                ListenPort = 6881,
                AdvertisedIp = "192.168.0.150",
                AdvertisedPort = 6881,
                MaxBlockLength = 16 * 1024,
                MaxOutstandingRequests = 8192,
                MaxConnections = 128,
                HandshakeTimeoutSeconds = 0,
                IdleTimeoutSeconds = 0
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:PeerWire:HandshakeTimeoutSeconds must be between 1 and 300.", result.Failures);
        Assert.Contains("Icecold:PeerWire:IdleTimeoutSeconds must be between 1 and 3600.", result.Failures);
    }

    [Fact]
    public void Validate_Fails_When_SuperSeed_Settings_Are_Invalid()
    {
        var result = new PeerWireOptionsValidator().Validate(null, new IcecoldOptions
        {
            PeerWire = new PeerWireOptions
            {
                Enabled = true,
                BindAddress = "0.0.0.0",
                ListenPort = 6881,
                AdvertisedIp = "192.168.0.150",
                AdvertisedPort = 6881,
                MaxBlockLength = 16 * 1024,
                MaxOutstandingRequests = 8192,
                MaxConnections = 128,
                SuperSeed = new PeerWireSuperSeedOptions
                {
                    Enabled = true,
                    MinimumPeerCount = -1,
                    MaxPiecesPerPeer = 0
                },
                Standby = new PeerWireStandbyOptions
                {
                    Enabled = true,
                    MinimumExternalSeeders = 0,
                    MinimumExternalSeederAgeSeconds = -1
                }
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:PeerWire:SuperSeed:MaxPiecesPerPeer must be between 1 and 64.", result.Failures);
        Assert.Contains("Icecold:PeerWire:SuperSeed:MinimumPeerCount must be between 0 and 100000.", result.Failures);
        Assert.Contains("Icecold:PeerWire:Standby:MinimumExternalSeeders must be between 1 and 100000.", result.Failures);
        Assert.Contains("Icecold:PeerWire:Standby:MinimumExternalSeederAgeSeconds must be between 0 and 86400.", result.Failures);
    }
}
