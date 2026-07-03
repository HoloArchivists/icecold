using Icecold.Api.Options;
using Icecold.Api.Tracker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Controllers;

[ApiController]
public sealed class TrackerController(
    TrackerAnnounceService announceTracker,
    TrackerScrapeService scrapeTracker,
    IOptions<IcecoldOptions> options) : ControllerBase
{
    [HttpGet("/announce")]
    public async Task<IActionResult> Announce(CancellationToken cancellationToken)
    {
        if (!options.Value.Tracker.Enabled)
            return NotFound();

        if (!TrackerQueryParser.TryParse(Request, options.Value.Tracker, out var announce, out var failureReason) || announce is null)
            return TrackerFailure(failureReason);

        var result = await announceTracker.AnnounceAsync(announce, cancellationToken);
        return result.Succeeded
            ? File(TrackerResponse.Success(result.Announce!, announce.Compact), "text/plain")
            : TrackerFailure(result.FailureReason!);
    }

    [HttpGet("/scrape")]
    public async Task<IActionResult> Scrape(CancellationToken cancellationToken)
    {
        if (!options.Value.Tracker.Enabled)
            return NotFound();

        if (!TrackerQueryParser.TryParseScrape(Request, out var infoHashes, out var failureReason))
            return TrackerFailure(failureReason);

        var result = await scrapeTracker.ScrapeAsync(infoHashes, cancellationToken);
        return result.Succeeded
            ? File(TrackerResponse.Scrape(result.Scrape!), "text/plain")
            : TrackerFailure(result.FailureReason!);
    }

    IActionResult TrackerFailure(string reason)
        => File(TrackerResponse.Failure(reason), "text/plain");
}
