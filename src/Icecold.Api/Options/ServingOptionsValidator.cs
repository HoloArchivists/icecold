using Microsoft.Extensions.Options;

namespace Icecold.Api.Options;

public sealed class ServingOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var failures = new List<string>();

        if (!options.WebSeed.Enabled && !options.PeerWire.Enabled)
            failures.Add("At least one serving transport must be enabled: Icecold:WebSeed:Enabled or Icecold:PeerWire:Enabled.");

        if (!options.Tracker.Enabled && !options.WebSeed.Enabled)
        {
            failures.Add(
                "Icecold:WebSeed:Enabled must be true when Icecold:Tracker:Enabled is false; Icecold peer-wire discovery depends on the built-in tracker.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
