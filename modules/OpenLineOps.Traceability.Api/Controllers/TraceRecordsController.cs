using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Application.Records;
using CreateApiArtifactRecordRequest = OpenLineOps.Traceability.Api.Models.CreateArtifactRecordRequest;
using CreateApiAuditEntryRequest = OpenLineOps.Traceability.Api.Models.CreateAuditEntryRequest;
using CreateApiMeasurementRecordRequest = OpenLineOps.Traceability.Api.Models.CreateMeasurementRecordRequest;
using CreateApiTraceRecordRequest = OpenLineOps.Traceability.Api.Models.CreateTraceRecordRequest;
using CreateApplicationArtifactRecordRequest = OpenLineOps.Traceability.Application.Records.CreateArtifactRecordRequest;
using CreateApplicationAuditEntryRequest = OpenLineOps.Traceability.Application.Records.CreateAuditEntryRequest;
using CreateApplicationMeasurementRecordRequest = OpenLineOps.Traceability.Application.Records.CreateMeasurementRecordRequest;
using CreateApplicationTraceRecordRequest = OpenLineOps.Traceability.Application.Records.CreateTraceRecordRequest;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.TraceabilityV1)]
[Route(OpenLineOpsApiRoutes.Traceability + "/records")]
public sealed class TraceRecordsController : ControllerBase
{
    private readonly ITraceRecordService _traceRecordService;

    public TraceRecordsController(ITraceRecordService traceRecordService)
    {
        _traceRecordService = traceRecordService;
    }

    [HttpPost]
    [ProducesResponseType<TraceRecordResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TraceRecordResponse>> CreateAsync(
        CreateApiTraceRecordRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var result = await _traceRecordService
            .CreateCompletedAsync(ToApplicationRequest(request), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = ToResponse(result.Value);

        return Created($"/api/traceability/records/{response.TraceRecordId}", response);
    }

    [HttpGet]
    [ProducesResponseType<PagedTraceRecordSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedTraceRecordSummaryResponse>> QueryAsync(
        [FromQuery] string? serialNumber,
        [FromQuery] string? batchId,
        [FromQuery] string? stationId,
        [FromQuery] string? fixtureId,
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] DateTimeOffset? completedFromUtc,
        [FromQuery] DateTimeOffset? completedToUtc,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _traceRecordService
            .QueryAsync(
                new TraceRecordQuery(
                    serialNumber,
                    batchId,
                    stationId,
                    fixtureId,
                    completedFromUtc,
                    completedToUtc,
                    new PagedRequest(pageNumber, pageSize),
                    projectId: projectId,
                    applicationId: applicationId,
                    projectSnapshotId: projectSnapshotId,
                    topologyId: topologyId),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(new PagedTraceRecordSummaryResponse(
                result.Value.Items.Select(ToSummaryResponse).ToArray(),
                result.Value.PageNumber,
                result.Value.PageSize,
                result.Value.TotalCount,
                result.Value.TotalPages));
    }

    [HttpGet("{traceRecordId:guid}")]
    [ProducesResponseType<TraceRecordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TraceRecordResponse>> GetByIdAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken)
    {
        var result = await _traceRecordService
            .GetByIdAsync(traceRecordId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    [HttpGet("{traceRecordId:guid}/export")]
    [ProducesResponseType<TraceRecordExportPackageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TraceRecordExportPackageResponse>> ExportAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken)
    {
        var result = await _traceRecordService
            .ExportAsync(traceRecordId, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(new TraceRecordExportPackageResponse(
                result.Value.PackageFormatVersion,
                result.Value.ExportedAtUtc,
                ToResponse(result.Value.TraceRecord)));
    }

    private static CreateApplicationTraceRecordRequest ToApplicationRequest(
        CreateApiTraceRecordRequest request)
    {
        return new CreateApplicationTraceRecordRequest(
            request.TraceRecordId,
            request.RuntimeSessionId,
            request.SerialNumber,
            request.BatchId,
            request.StationId,
            request.FixtureId,
            request.ProcessDefinitionId,
            request.ProcessVersionId,
            request.ConfigurationSnapshotId,
            request.RecipeSnapshotId,
            request.DeviceId,
            request.Judgement,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.RecordedBy,
            request.Measurements?.Select(ToApplicationRequest).ToArray(),
            request.Artifacts?.Select(ToApplicationRequest).ToArray(),
            request.AuditEntries?.Select(ToApplicationRequest).ToArray(),
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId);
    }

    private static CreateApplicationMeasurementRecordRequest ToApplicationRequest(
        CreateApiMeasurementRecordRequest request)
    {
        return new CreateApplicationMeasurementRecordRequest(
            request.MeasurementRecordId,
            request.Name,
            request.NumericValue,
            request.TextValue,
            request.Unit,
            request.DeviceId,
            request.RuntimeCommandId,
            request.Passed,
            request.MeasuredAtUtc);
    }

    private static CreateApplicationArtifactRecordRequest ToApplicationRequest(
        CreateApiArtifactRecordRequest request)
    {
        return new CreateApplicationArtifactRecordRequest(
            request.ArtifactRecordId,
            request.Name,
            request.Kind,
            request.StorageKey,
            request.MediaType,
            request.SizeBytes,
            request.Sha256,
            request.DeviceId,
            request.CapturedAtUtc);
    }

    private static CreateApplicationAuditEntryRequest ToApplicationRequest(
        CreateApiAuditEntryRequest request)
    {
        return new CreateApplicationAuditEntryRequest(
            request.AuditEntryId,
            request.ActorId,
            request.Action,
            request.Detail,
            request.OccurredAtUtc);
    }

    private static TraceRecordResponse ToResponse(TraceRecordDetails details)
    {
        return new TraceRecordResponse(
            details.TraceRecordId,
            details.RuntimeSessionId,
            details.ProjectId,
            details.ApplicationId,
            details.ProjectSnapshotId,
            details.TopologyId,
            details.SerialNumber,
            details.BatchId,
            details.StationId,
            details.FixtureId,
            details.ProcessDefinitionId,
            details.ProcessVersionId,
            details.ConfigurationSnapshotId,
            details.RecipeSnapshotId,
            details.DeviceId,
            details.Judgement,
            details.StartedAtUtc,
            details.CompletedAtUtc,
            details.RecordedBy,
            details.Measurements.Select(ToResponse).ToArray(),
            details.Artifacts.Select(ToResponse).ToArray(),
            details.AuditEntries.Select(ToResponse).ToArray());
    }

    private static TraceRecordSummaryResponse ToSummaryResponse(TraceRecordSummary summary)
    {
        return new TraceRecordSummaryResponse(
            summary.TraceRecordId,
            summary.RuntimeSessionId,
            summary.ProjectId,
            summary.ApplicationId,
            summary.ProjectSnapshotId,
            summary.TopologyId,
            summary.SerialNumber,
            summary.BatchId,
            summary.StationId,
            summary.FixtureId,
            summary.ProcessVersionId,
            summary.ConfigurationSnapshotId,
            summary.RecipeSnapshotId,
            summary.DeviceId,
            summary.Judgement,
            summary.CompletedAtUtc);
    }

    private static MeasurementRecordResponse ToResponse(MeasurementRecordDetails measurement)
    {
        return new MeasurementRecordResponse(
            measurement.MeasurementRecordId,
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId,
            measurement.RuntimeCommandId,
            measurement.Passed,
            measurement.MeasuredAtUtc);
    }

    private static ArtifactRecordResponse ToResponse(ArtifactRecordDetails artifact)
    {
        return new ArtifactRecordResponse(
            artifact.ArtifactRecordId,
            artifact.Name,
            artifact.Kind,
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.DeviceId,
            artifact.CapturedAtUtc);
    }

    private static AuditEntryResponse ToResponse(AuditEntryDetails auditEntry)
    {
        return new AuditEntryResponse(
            auditEntry.AuditEntryId,
            auditEntry.ActorId,
            auditEntry.Action,
            auditEntry.Detail,
            auditEntry.OccurredAtUtc);
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

    private static Dictionary<string, string[]> Validate(CreateApiTraceRecordRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        if (request.TraceRecordId == Guid.Empty)
        {
            errors[nameof(request.TraceRecordId)] = ["TraceRecordId cannot be an empty GUID."];
        }

        if (request.RuntimeSessionId == Guid.Empty)
        {
            errors[nameof(request.RuntimeSessionId)] = ["RuntimeSessionId is required."];
        }

        if (request.StartedAtUtc == default)
        {
            errors[nameof(request.StartedAtUtc)] = ["StartedAtUtc is required."];
        }

        if (request.CompletedAtUtc == default)
        {
            errors[nameof(request.CompletedAtUtc)] = ["CompletedAtUtc is required."];
        }

        if (request.CompletedAtUtc < request.StartedAtUtc)
        {
            errors[nameof(request.CompletedAtUtc)] = ["CompletedAtUtc cannot be earlier than StartedAtUtc."];
        }

        AddRequired(errors, nameof(request.SerialNumber), request.SerialNumber);
        AddRequired(errors, nameof(request.StationId), request.StationId);
        AddRequired(errors, nameof(request.ProcessDefinitionId), request.ProcessDefinitionId);
        AddRequired(errors, nameof(request.ProcessVersionId), request.ProcessVersionId);
        AddRequired(errors, nameof(request.ConfigurationSnapshotId), request.ConfigurationSnapshotId);
        AddRequired(errors, nameof(request.RecipeSnapshotId), request.RecipeSnapshotId);
        AddRequired(errors, nameof(request.DeviceId), request.DeviceId);
        AddRequired(errors, nameof(request.RecordedBy), request.RecordedBy);

        return errors;
    }

    private static void AddRequired(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = ["Value is required."];
        }
    }
}
