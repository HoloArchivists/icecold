using Microsoft.Extensions.Options;

namespace Icecold.Api.Options;

public sealed class ServingOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        return options.WebSeed.Enabled || options.PeerWire.Enabled
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("At least one serving transport must be enabled: Icecold:WebSeed:Enabled or Icecold:PeerWire:Enabled.");
    }
}
