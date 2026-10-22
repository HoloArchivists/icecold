using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Stats;

public sealed class StatsOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        // At least 1s so /stats cannot force a full tracker peer-store scan on every request.
        return options.Stats.CacheSeconds is >= 1 and <= 300
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Icecold:Stats:CacheSeconds must be between 1 and 300.");
    }
}
