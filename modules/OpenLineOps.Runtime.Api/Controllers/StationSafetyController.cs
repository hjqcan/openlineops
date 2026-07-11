using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Safety;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
public sealed class StationSafetyController(StationEmergencyStopService service) : ControllerBase
{
    private static readonly HashSet<string> SafetyEventQueryFields =
        new(StringComparer.Ordinal)
        {
            "projectId",
            "applicationId",
            "projectSnapshotId",
            "stationSystemId"
        };

    [HttpPost(OpenLineOpsApiRoutes.OperationsStationEmergencyStop)]
    [ProducesResponseType<StationEmergencyStopApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<StationEmergencyStopApiResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationEmergencyStopApiResponse>> RequestEmergencyStopAsync(
        string stationSystemId,
        RequestStationEmergencyStopApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var submission = await service.RequestAsync(
                    new RequestStationEmergencyStop(
                        ParseUuid(request.MessageId, nameof(request.MessageId)),
                        request.IdempotencyKey,
                        request.ProjectId,
                        request.ApplicationId,
                        request.ProjectSnapshotId,
                        stationSystemId,
                        request.ActorId,
                        request.Reason,
                        ParseUtc(request.RequestedAtUtc, nameof(request.RequestedAtUtc))),
                    cancellationToken)
                .ConfigureAwait(false);
            var response = ToResponse(submission.Record, submission.Replayed);
            return submission.Record.Status == StationEmergencyStopStatus.Pending
                ? Accepted(response)
                : Ok(response);
        }
        catch (StationEmergencyStopIdempotencyConflictException exception)
        {
            return Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Runtime.EmergencyStopIdempotencyConflict",
                exception.Message));
        }
        catch (StationEmergencyStopDeploymentException exception)
        {
            return NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Runtime.StationDeploymentNotFound",
                exception.Message));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpGet(OpenLineOpsApiRoutes.OperationsSafetyEvents)]
    [ProducesResponseType<StationSafetyEventsApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationSafetyEventsApiResponse>> ListSafetyEventsAsync(
        [FromQuery] string projectId,
        [FromQuery] string applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(key => !SafetyEventQueryFields.Contains(key))
            || Request.Query.Any(parameter => parameter.Value.Count != 1))
        {
            return BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Validation.StrictQuery",
                "Safety event query contains an unknown or repeated field."));
        }

        try
        {
            var records = await service.ListAsync(
                    new StationEmergencyStopQuery(
                        projectId,
                        applicationId,
                        projectSnapshotId,
                        stationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
            return Ok(new StationSafetyEventsApiResponse(records
                .Select(record => ToResponse(record, replayed: false))
                .ToArray()));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    private static StationEmergencyStopApiResponse ToResponse(
        StationEmergencyStopRecord record,
        bool replayed)
    {
        var request = record.Request;
        var acknowledgement = record.Acknowledgement;
        return new StationEmergencyStopApiResponse(
            request.MessageId,
            request.IdempotencyKey,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.StationSystemId,
            request.AgentId,
            request.StationId,
            request.RelatedProductionRunIds,
            request.ActorId,
            request.Reason,
            FormatUtc(request.RequestedAtUtc),
            record.Status.ToString(),
            acknowledgement?.MessageId,
            acknowledgement is null ? null : FormatUtc(acknowledgement.AcknowledgedAtUtc),
            acknowledgement?.FailureCode,
            acknowledgement?.FailureReason,
            record.DispatchAttemptCount,
            record.LastDispatchFailure,
            FormatUtc(record.LastUpdatedAtUtc),
            replayed,
            record.Evidence.Select(evidence => new StationSafetyEvidenceApiResponse(
                evidence.Sequence,
                evidence.Kind.ToString(),
                evidence.MessageId,
                FormatUtc(evidence.OccurredAtUtc),
                evidence.FailureCode,
                evidence.FailureReason)).ToArray());
    }

    private static Guid ParseUuid(string value, string name) =>
        StationSafetyCanonical.RequireLowercaseUuid(value, name);

    private static DateTimeOffset ParseUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{name} must be a canonical yyyy-MM-ddTHH:mm:ss.fffZ UTC timestamp.",
                name);
        }

        return parsed;
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);

    private static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail
    };
}
