using Icecold.Api.Tracker;
using Microsoft.AspNetCore.Mvc;

namespace Icecold.Api.Controllers;

[ApiController]
public sealed class TrackerController(TrackerAnnounceService tracker) : ControllerBase
{
    [HttpGet("/announce")]
    public async Task<IActionResult> Announce(CancellationToken cancellationToken)
    {
        if (!TrackerQueryParser.TryParse(Request, out var announce, out var failureReason) || announce is null)
            return TrackerFailure(failureReason);

        var result = await tracker.AnnounceAsync(announce, cancellationToken);
        return result.Succeeded
            ? File(TrackerResponse.Success(result.Announce!, announce.Compact), "text/plain")
            : TrackerFailure(result.FailureReason!);
    }

    IActionResult TrackerFailure(string reason)
        => File(TrackerResponse.Failure(reason), "text/plain");
}
