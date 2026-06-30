using Icecold.Api.Options;

namespace Icecold.Tests;

public sealed class ServingOptionsTests
{
    [Fact]
    public void Validate_Fails_When_All_Serving_Transports_Are_Disabled()
    {
        var result = new ServingOptionsValidator().Validate(null, new IcecoldOptions
        {
            WebSeed = new WebSeedOptions { Enabled = false },
            PeerWire = new PeerWireOptions { Enabled = false }
        });

        Assert.True(result.Failed);
        Assert.Contains(
            "At least one serving transport must be enabled: Icecold:WebSeed:Enabled or Icecold:PeerWire:Enabled.",
            result.Failures);
    }

    [Fact]
    public void Validate_Allows_Either_Serving_Transport()
    {
        var validator = new ServingOptionsValidator();

        Assert.False(validator.Validate(null, new IcecoldOptions
        {
            WebSeed = new WebSeedOptions { Enabled = true },
            PeerWire = new PeerWireOptions { Enabled = false }
        }).Failed);

        Assert.False(validator.Validate(null, new IcecoldOptions
        {
            WebSeed = new WebSeedOptions { Enabled = false },
            PeerWire = new PeerWireOptions { Enabled = true }
        }).Failed);
    }
}
