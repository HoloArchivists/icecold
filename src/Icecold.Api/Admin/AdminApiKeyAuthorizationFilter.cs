using Icecold.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Admin;

public sealed class AdminApiKeyAuthorizationFilter(IOptions<IcecoldOptions> options) : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Icecold-Admin-Key";

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var expected = options.Value.AdminApiKey;
        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Detail = "Icecold:AdminApiKey must be configured"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return Task.CompletedTask;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || provided.Count != 1
            || !string.Equals(provided[0], expected, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult();
        }

        return Task.CompletedTask;
    }
}
