using Icecold.Api.Api;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc;

namespace Icecold.Api.Controllers;

[ApiController]
[Route("torrents")]
public sealed class PublicTorrentsController(TorrentMetadataService torrents) : ControllerBase
{
    const string InvalidInfoHashMessage = "infoHash must be a 40 character hex SHA-1 info hash";

    [HttpGet("{infoHash}.torrent")]
    public async Task<IActionResult> GetTorrentFile(string infoHash, CancellationToken cancellationToken)
    {
        if (!InfoHashUtil.IsHexInfoHash(infoHash))
            return BadRequest(new ProblemDetailsDto(InvalidInfoHashMessage));

        var torrent = await torrents.GetTorrentFileAsync(infoHash, cancellationToken);
        return torrent is null
            ? NotFound()
            : File(torrent.Bytes, "application/x-bittorrent", torrent.FileName);
    }

    [HttpGet("{infoHash}/magnet")]
    public async Task<IActionResult> GetMagnet(string infoHash, CancellationToken cancellationToken)
    {
        if (!InfoHashUtil.IsHexInfoHash(infoHash))
            return BadRequest(new ProblemDetailsDto(InvalidInfoHashMessage));

        var magnet = await torrents.GetMagnetAsync(infoHash, cancellationToken);
        return magnet is null ? NotFound() : Content(magnet, "text/plain");
    }
}
