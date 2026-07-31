using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/artifacts")]
public sealed class TraceArtifactsController : ControllerBase
{
    private readonly ITraceArtifactStorage _artifactStorage;
    private readonly IStationArtifactUploadAuthorizer _uploadAuthorizer;
    private readonly IStationArtifactReceiptService _receiptService;

    public TraceArtifactsController(
        ITraceArtifactStorage artifactStorage,
        IStationArtifactUploadAuthorizer uploadAuthorizer,
        IStationArtifactReceiptService receiptService)
    {
        _artifactStorage = artifactStorage;
        _uploadAuthorizer = uploadAuthorizer;
        _receiptService = receiptService;
    }

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<StationArtifactReceiptResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<StationArtifactReceiptResponse>> StoreAsync(
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                Request.ContentType,
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new ProblemDetails
                {
                    Title = "StationArtifact.ContentTypeRejected",
                    Detail = "Station artifact uploads require application/octet-stream.",
                    Status = StatusCodes.Status415UnsupportedMediaType
                });
        }

        if (!TryReadSingleHeader(
                StationArtifactUploadProtocol.AgentIdHeader,
                StationArtifactReceiptIdentity.MaximumAgentIdLength,
                out var agentId)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.StationIdHeader,
                StationArtifactReceiptIdentity.MaximumStationIdLength,
                out var stationId)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.JobIdHeader,
                36,
                out var jobIdText)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.ArtifactNameHeader,
                StationArtifactUploadProtocol.MaximumEncodedArtifactNameHeaderLength,
                out var encodedName)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.ArtifactKindHeader,
                StationArtifactUploadProtocol.MaximumEncodedArtifactKindHeaderLength,
                out var encodedKind)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.ArtifactSizeHeader,
                20,
                out var sizeText)
            || !TryReadSingleHeader(
                StationArtifactUploadProtocol.ArtifactSha256Header,
                64,
                out var sha256))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["headers"] = ["All Station artifact identity, size, and hash headers are required exactly once."]
            }));
        }

        if (!string.Equals(User.GetRequiredActorId(), agentId, StringComparison.Ordinal)
            || !string.Equals(User.GetRequiredStationId(), stationId, StringComparison.Ordinal))
        {
            return Forbid();
        }

        if (!Guid.TryParseExact(jobIdText, "D", out var jobId)
            || jobId == Guid.Empty
            || !string.Equals(jobId.ToString("D"), jobIdText, StringComparison.Ordinal)
            || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var sizeBytes)
            || sizeBytes < 0
            || Request.ContentLength != sizeBytes)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["headers"] = ["Job id, declared size, and HTTP Content-Length must be canonical and exact."]
            }));
        }

        string artifactName;
        string artifactKind;
        string? mediaType = null;
        try
        {
            artifactName = StationArtifactUploadProtocol.DecodeArtifactName(encodedName);
            artifactKind = StationArtifactUploadProtocol.DecodeArtifactKind(encodedKind);
            if (Request.Headers.TryGetValue(
                    StationArtifactUploadProtocol.ArtifactMediaTypeHeader,
                    out var mediaTypeValues))
            {
                if (mediaTypeValues.Count != 1
                    || mediaTypeValues[0] is not { Length: > 0 } encodedMediaType
                    || encodedMediaType.Length
                        > StationArtifactUploadProtocol.MaximumEncodedMediaTypeHeaderLength)
                {
                    throw new InvalidDataException(
                        "Station artifact media type header must occur at most once with a canonical value.");
                }

                mediaType = StationArtifactUploadProtocol.DecodeMediaType(encodedMediaType);
            }
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [StationArtifactUploadProtocol.ArtifactNameHeader] = [exception.Message]
            }));
        }

        var authorization = await _uploadAuthorizer
            .AuthorizeAsync(
                new StationArtifactUploadAuthorizationRequest(
                    agentId,
                    stationId,
                    jobId,
                    artifactName,
                    artifactKind,
                    mediaType,
                    sizeBytes,
                    sha256),
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Status != StationArtifactUploadAuthorizationStatus.Authorized)
        {
            return authorization.Status switch
            {
                StationArtifactUploadAuthorizationStatus.MetadataInvalid => Problem(
                    title: authorization.FailureCode,
                    detail: "The Station artifact metadata is not canonical.",
                    statusCode: StatusCodes.Status400BadRequest),
                StationArtifactUploadAuthorizationStatus.JobNotFound => Problem(
                    title: authorization.FailureCode,
                    detail: "The Station job does not exist.",
                    statusCode: StatusCodes.Status404NotFound),
                StationArtifactUploadAuthorizationStatus.IdentityForbidden => Problem(
                    title: authorization.FailureCode,
                    detail: "The Station job does not belong to this Agent identity.",
                    statusCode: StatusCodes.Status403Forbidden),
                StationArtifactUploadAuthorizationStatus.TerminalConflict => Problem(
                    title: authorization.FailureCode,
                    detail: "The Station job is terminal and does not authorize this artifact metadata.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException(
                    "Station artifact upload authorization returned an unknown status.")
            };
        }

        var result = await _receiptService
            .StoreAsync(
                new StoreStationArtifactRequest(
                    agentId,
                    stationId,
                    jobId,
                    artifactName,
                    artifactKind,
                    mediaType,
                    sizeBytes,
                    sha256,
                    Request.Body),
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
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
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

    private static StationArtifactReceiptResponse ToResponse(StationArtifactReceipt receipt) => new(
        receipt.ReceiptId,
        receipt.AgentId,
        receipt.StationId,
        receipt.JobId,
        receipt.ArtifactName,
        receipt.ArtifactKind,
        receipt.MediaType,
        receipt.SizeBytes,
        receipt.Sha256,
        receipt.StorageKey);

    private bool TryReadSingleHeader(string name, int maximumLength, out string value)
    {
        if (Request.Headers.TryGetValue(name, out var values)
            && values.Count == 1
            && values[0] is { Length: > 0 } single
            && single.Length <= maximumLength)
        {
            value = single;
            return true;
        }

        value = string.Empty;
        return false;
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
