using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Indexing;

public sealed class IndexingOptionsValidator : IValidateOptions<IcecoldOptions>
{
    public ValidateOptionsResult Validate(string? name, IcecoldOptions options)
    {
        var failures = new List<string>();
        if (options.Indexing.MaxConcurrency < 1)
            failures.Add("Icecold:Indexing:MaxConcurrency must be at least 1.");

        if (options.Indexing.PollIntervalSeconds < 1)
            failures.Add("Icecold:Indexing:PollIntervalSeconds must be at least 1.");

        if (options.Indexing.LeaseDurationSeconds < 10)
            failures.Add("Icecold:Indexing:LeaseDurationSeconds must be at least 10.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
