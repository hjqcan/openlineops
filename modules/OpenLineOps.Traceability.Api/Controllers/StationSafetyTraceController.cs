using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Application.Safety;
using OpenLineOps.Traceability.Api.Models;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/station-safety-evidence")]
public sealed class StationSafetyTraceController(
    IStationEmergencyStopRepository repository) : ControllerBase
{
    private static readonly HashSet<string> QueryFields =
        new(StringComparer.Ordinal)
        {
            "projectId",
            "applicationId",
            "projectSnapshotId",
            "stationSystemId"
        };

    [HttpGet]
    [ProducesResponseType<StationSafetyTraceSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationSafetyTraceSearchResponse>> SearchAsync(
        [FromQuery] string projectId,
        [FromQuery] string applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(key => !QueryFields.Contains(key))
            || Request.Query.Any(parameter => parameter.Value.Count != 1))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation.StrictQuery",
                Detail = "Station safety Trace query contains an unknown or repeated field."
            });
        }

        try
        {
            StationSafetyCanonical.RequireText(projectId, nameof(projectId));
            StationSafetyCanonical.RequireText(applicationId, nameof(applicationId));
            StationSafetyCanonical.RequireOptionalText(projectSnapshotId, nameof(projectSnapshotId));
            StationSafetyCanonical.RequireOptionalText(stationSystemId, nameof(stationSystemId));
            var records = await repository.ListAsync(
                    new StationEmergencyStopQuery(
                        projectId,
                        applicationId,
                        projectSnapshotId,
                        stationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(new StationSafetyTraceSearchResponse(records.Select(ToResponse).ToArray()));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    private static StationEmergencyStopTraceResponse ToResponse(
        StationEmergencyStopRecord record) => new(
        record.Request.MessageId,
        record.Request.IdempotencyKey,
        record.Request.ProjectId,
        record.Request.ApplicationId,
        record.Request.ProjectSnapshotId,
        record.Request.StationSystemId,
        record.Request.AgentId,
        record.Request.StationId,
        record.Request.RelatedProductionRunIds,
        record.Request.ActorId,
        record.Request.Reason,
        record.Request.RequestedAtUtc,
        record.Status.ToString(),
        record.Acknowledgement?.MessageId,
        record.Acknowledgement?.AcknowledgedAtUtc,
        record.Acknowledgement?.FailureCode,
        record.Acknowledgement?.FailureReason,
        record.DispatchAttemptCount,
        record.LastDispatchFailure,
        record.LastUpdatedAtUtc,
        record.Evidence.Select(evidence => new StationSafetyTraceEvidenceResponse(
            evidence.Sequence,
            evidence.Kind.ToString(),
            evidence.MessageId,
            evidence.OccurredAtUtc,
            evidence.FailureCode,
            evidence.FailureReason)).ToArray());
}
