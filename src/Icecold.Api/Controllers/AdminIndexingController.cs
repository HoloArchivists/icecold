using Icecold.Api.Admin;
using Icecold.Api.Api;
using Icecold.Api.Indexing;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Mvc;

namespace Icecold.Api.Controllers;

[ApiController]
[AdminApiKey]
[Route("api/index")]
public sealed class AdminIndexingController(IndexFileService indexing, IndexingQueue queue) : ControllerBase
{
    [HttpPost("file")]
    public async Task<IActionResult> IndexFile(IndexFileRequest request, CancellationToken cancellationToken)
    {
        var result = await indexing.SubmitFileAsync(
            new IndexFileCommand(request.Source, request.Path, request.DisplayName, request.Priority),
            cancellationToken);

        return await ToActionResultAsync(result, cancellationToken);
    }

    [HttpPost("files")]
    public async Task<IActionResult> IndexFiles(IndexFilesRequest request, CancellationToken cancellationToken)
    {
        var result = await indexing.SubmitFilesAsync(
            new IndexFilesCommand(
                request.Source,
                request.Name,
                request.Files?.Select(f => new IndexFilesEntry(f.Path, f.TorrentPath)).ToList(),
                request.Priority),
            cancellationToken);

        return await ToActionResultAsync(result, cancellationToken);
    }

    [HttpPost("folder")]
    public IActionResult IndexFolder()
        => StatusCode(StatusCodes.Status501NotImplemented);

    async Task<IActionResult> ToActionResultAsync(IndexFileSubmission result, CancellationToken cancellationToken)
        => result.Status switch
        {
            IndexFileSubmissionStatus.Accepted => Accepted(
                $"/api/torrents/{result.Torrent!.Id}",
                IndexResponse.From(result.Torrent, await queue.GetQueuePositionAsync(result.Torrent, cancellationToken))),
            IndexFileSubmissionStatus.Completed => Ok(IndexResponse.From(result.Torrent!)),
            IndexFileSubmissionStatus.NotFound => NotFound(new ProblemDetailsDto(result.Error!)),
            IndexFileSubmissionStatus.BadRequest => BadRequest(new ProblemDetailsDto(result.Error!)),
            IndexFileSubmissionStatus.Conflict => Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Problem("Unexpected index submission result.")
        };
}
