using Icecold.Api.Admin;
using Icecold.Api.Api;
using Icecold.Api.Indexing;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc;

namespace Icecold.Api.Controllers;

[ApiController]
[AdminApiKey]
[Route("api/index")]
public sealed class AdminIndexingController(IndexFileService indexing) : ControllerBase
{
    [HttpPost("file")]
    public async Task<IActionResult> IndexFile(IndexFileRequest request, CancellationToken cancellationToken)
    {
        var result = await indexing.SubmitFileAsync(
            new IndexFileCommand(request.Source, request.Path, request.DisplayName),
            cancellationToken);

        return result.Status switch
        {
            IndexFileSubmissionStatus.Accepted => Accepted(
                $"/api/torrents/{result.Torrent!.Id}",
                IndexResponse.From(result.Torrent)),
            IndexFileSubmissionStatus.Completed => Ok(IndexResponse.From(result.Torrent!)),
            IndexFileSubmissionStatus.NotFound => NotFound(new ProblemDetailsDto(result.Error!)),
            IndexFileSubmissionStatus.BadRequest => BadRequest(new ProblemDetailsDto(result.Error!)),
            IndexFileSubmissionStatus.Conflict => Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Problem("Unexpected index submission result.")
        };
    }

    [HttpPost("folder")]
    public IActionResult IndexFolder()
        => StatusCode(StatusCodes.Status501NotImplemented);
}
