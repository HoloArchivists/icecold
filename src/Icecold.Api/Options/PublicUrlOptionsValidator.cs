using Microsoft.Extensions.Options;

namespace Icecold.Api.Options;

public sealed class PublicUrlOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var failures = new List<string>();

        if (!IsValidPublicBaseUrl(options.PublicBaseUrl))
            failures.Add("Icecold:PublicBaseUrl must be an absolute http or https URL without a query string or fragment.");

        // Empty falls back to Icecold:PublicBaseUrl.
        if (!string.IsNullOrWhiteSpace(options.WebSeed.PublicBaseUrl) && !IsValidPublicBaseUrl(options.WebSeed.PublicBaseUrl))
            failures.Add("Icecold:WebSeed:PublicBaseUrl must be empty or an absolute http or https URL without a query string or fragment.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // Path prefixes are allowed (for example a CDN path); query strings and fragments would break appended paths.
    static bool IsValidPublicBaseUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.Contains('?')
            && !value.Contains('#')
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(uri.Host);
}
