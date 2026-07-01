using Icecold.Api.Options;
using Icecold.Api.Stats;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Icecold.Api.Controllers;

[ApiController]
public sealed class PublicStatsController(
    PublicStatsService stats,
    IOptions<IcecoldOptions> options) : ControllerBase
{
    [HttpGet("/stats")]
    public async Task<ActionResult<PublicStatsResponse>> Get(CancellationToken cancellationToken)
    {
        if (!options.Value.Stats.Enabled)
            return NotFound();

        var result = await stats.GetAsync(cancellationToken);
        if (result.CacheSeconds > 0)
            Response.Headers[HeaderNames.CacheControl] = $"public, max-age={result.CacheSeconds}";
        else
            Response.Headers[HeaderNames.CacheControl] = "no-store";

        return Ok(result);
    }
}
