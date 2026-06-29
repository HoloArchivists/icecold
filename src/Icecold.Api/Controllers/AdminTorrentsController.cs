using Icecold.Api.Admin;
using Icecold.Api.Data;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Icecold.Api.Controllers;

[ApiController]
[AdminApiKey]
[Route("api/torrents")]
public sealed class AdminTorrentsController(IcecoldDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return torrent is null ? NotFound() : Ok(IndexResponse.From(torrent));
    }
}
