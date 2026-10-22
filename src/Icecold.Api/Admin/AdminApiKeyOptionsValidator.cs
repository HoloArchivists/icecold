using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Admin;

// Outside Production a missing key only fails admin requests closed; in Production it is a startup error.
public sealed class AdminApiKeyOptionsValidator(IHostEnvironment environment) : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        return environment.IsProduction() && string.IsNullOrWhiteSpace(options.AdminApiKey)
            ? ValidateOptionsResult.Fail("Icecold:AdminApiKey must be configured in Production.")
            : ValidateOptionsResult.Success;
    }
}
