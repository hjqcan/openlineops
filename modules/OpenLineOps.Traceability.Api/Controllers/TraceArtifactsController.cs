using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TraceabilityV1)]
[Route(OpenLineOpsApiRoutes.Traceability + "/artifacts")]
public sealed class TraceArtifactsController : ControllerBase
{
    private readonly ITraceArtifactStorage _artifactStorage;

    public TraceArtifactsController(ITraceArtifactStorage artifactStorage)
    {
        _artifactStorage = artifactStorage;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<StoredTraceArtifactResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StoredTraceArtifactResponse>> StoreAsync(
        IFormFile? file,
        [FromForm] string? storageKey,
        [FromForm] string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(file)] = ["Artifact file is required."]
            }));
        }

        await using var content = file.OpenReadStream();
        var result = await _artifactStorage
            .StoreAsync(
                new StoreTraceArtifactRequest(
                    storageKey,
                    file.FileName,
                    file.ContentType,
                    content,
                    expectedSha256),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/traceability/artifacts/{response.StorageKey}", response);
    }

    [HttpGet("{**storageKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        var result = await _artifactStorage
            .OpenReadAsync(storageKey, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var content = result.Value;
        return File(
            content.Content,
            content.MediaType ?? "application/octet-stream",
            content.FileName,
            enableRangeProcessing: true);
    }

    private static StoredTraceArtifactResponse ToResponse(StoredTraceArtifact artifact)
    {
        return new StoredTraceArtifactResponse(
            artifact.StorageKey,
            artifact.FileName,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256);
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }
}
