using Icecold.Api.Admin;
using Icecold.Api.Api;
using Icecold.Api.Data;
using Icecold.Api.Indexing;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Controllers;

[ApiController]
[AdminApiKey]
[Route("api/torrents")]
public sealed class AdminTorrentsController(
    IcecoldDbContext db,
    IndexingQueue queue,
    TorrentLocationService locations) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents.AsNoTracking()
            .Include(t => t.Locations)
            .Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return torrent is null
            ? NotFound()
            : Ok(IndexResponse.From(torrent, await queue.GetQueuePositionAsync(torrent, cancellationToken)));
    }

    // Sets the priority of a queued job exactly (it may also lower it). Higher runs first.
    [HttpPut("{id:guid}/priority")]
    public async Task<IActionResult> SetPriority(Guid id, SetPriorityRequest request, CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents
            .Include(t => t.Locations)
            .Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (torrent is null)
            return NotFound();

        if (torrent.Status is not (TorrentStatus.Pending or TorrentStatus.Hashing))
            return Problem($"Torrent is {torrent.Status}, not queued.", statusCode: StatusCodes.Status409Conflict);

        // EF writes only the changed column, so this does not race the worker's lease updates.
        torrent.Priority = request.Priority;
        await db.SaveChangesAsync(cancellationToken);

        return Ok(IndexResponse.From(torrent, await queue.GetQueuePositionAsync(torrent, cancellationToken)));
    }

    [HttpGet("{id:guid}/locations")]
    public async Task<IActionResult> GetLocations(Guid id, CancellationToken cancellationToken)
    {
        var result = await locations.GetLocationsAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/locations")]
    public async Task<IActionResult> AddLocation(
        Guid id,
        AddTorrentLocationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await locations.AddLocationAsync(id, request, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/locations/{locationId:guid}/primary")]
    public async Task<IActionResult> SetPrimary(
        Guid id,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var result = await locations.SetPrimaryAsync(id, locationId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpDelete("{id:guid}/locations/{locationId:guid}")]
    public async Task<IActionResult> DisableLocation(
        Guid id,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var result = await locations.DisableAsync(id, locationId, cancellationToken);
        return ToActionResult(result);
    }

    IActionResult ToActionResult(TorrentLocationOperationResult result)
        => result.Status switch
        {
            TorrentLocationOperationStatus.Success => Ok(result.Location),
            TorrentLocationOperationStatus.NotFound => NotFound(result.Error is null ? null : new ProblemDetailsDto(result.Error)),
            TorrentLocationOperationStatus.BadRequest => BadRequest(new ProblemDetailsDto(result.Error!)),
            TorrentLocationOperationStatus.Conflict => Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Problem("Unexpected torrent location operation result.")
        };
}
