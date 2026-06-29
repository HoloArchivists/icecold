using Icecold.Api.Api;
using Icecold.Api.Torrents;
using Icecold.Api.WebSeed;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Icecold.Api.Controllers;

[ApiController]
public sealed class WebSeedController(WebSeedService webSeed) : ControllerBase
{
    const string InvalidInfoHashMessage = "infoHash must be a 40 character hex SHA-1 info hash";

    [HttpGet("/webseed/{infoHash}/{**fileName}")]
    public async Task<IActionResult> Get(string infoHash, string? fileName, CancellationToken cancellationToken)
    {
        if (!InfoHashUtil.IsHexInfoHash(infoHash))
            return BadRequest(new ProblemDetailsDto(InvalidInfoHashMessage));

        WebSeedOpenResult result;
        try
        {
            result = await webSeed.OpenAsync(infoHash, Request.Headers[HeaderNames.Range].ToString(), cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            return BadRequest(new ProblemDetailsDto(ex.Message));
        }

        if (result.Status == WebSeedOpenStatus.NotFound)
            return NotFound();

        if (result.Status == WebSeedOpenStatus.Conflict)
            return Problem(result.Error, statusCode: StatusCodes.Status409Conflict);

        Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        Response.ContentType = "application/octet-stream";
        Response.ContentLength = result.Length;

        if (result.Partial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers[HeaderNames.ContentRange] = $"bytes {result.Offset}-{result.Offset + result.Length - 1}/{result.ContentLength}";
        }

        await using var stream = result.Stream!;
        await stream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }
}
