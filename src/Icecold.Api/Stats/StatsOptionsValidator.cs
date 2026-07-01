using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Stats;

public sealed class StatsOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        return options.Stats.CacheSeconds is >= 0 and <= 300
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Icecold:Stats:CacheSeconds must be between 0 and 300.");
    }
}
