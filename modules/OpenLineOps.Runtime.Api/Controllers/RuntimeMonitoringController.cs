using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Api.Hubs;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.RuntimeMonitoring)]
public sealed class RuntimeMonitoringController : ControllerBase
{
    private readonly IRuntimeMonitoringService _monitoringService;
    private readonly IClock _clock;
    private readonly IHubContext<RuntimeProgressHub, IRuntimeProgressClient> _hubContext;

    public RuntimeMonitoringController(
        IRuntimeMonitoringService monitoringService,
        IClock clock,
        IHubContext<RuntimeProgressHub, IRuntimeProgressClient> hubContext)
    {
        _monitoringService = monitoringService;
        _clock = clock;
        _hubContext = hubContext;
    }

    [HttpGet("stations")]
    [ProducesResponseType<RuntimeStationStatusesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeStationStatusesResponse>> GetStationStatusesAsync(
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] string? productionRunId,
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        var scopeResult = CreateMonitoringScope(
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionRunId);
        if (scopeResult.IsFailure)
        {
            return ToProblem(scopeResult.Error);
        }

        var stationSystemIdError = ValidateOptionalCanonical(
            stationSystemId,
            nameof(stationSystemId));
        if (stationSystemIdError is not null)
        {
            return ToProblem(stationSystemIdError);
        }

        var statuses = await _monitoringService
            .GetStationStatusesAsync(scopeResult.Value, stationSystemId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeStationStatusesResponse(
            statuses.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpGet("targets")]
    [ProducesResponseType<RuntimeTargetStatusesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeTargetStatusesResponse>> GetTargetStatusesAsync(
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] string? productionRunId,
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        var scopeResult = CreateMonitoringScope(
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionRunId);
        if (scopeResult.IsFailure)
        {
            return ToProblem(scopeResult.Error);
        }

        var stationSystemIdError = ValidateOptionalCanonical(
            stationSystemId,
            nameof(stationSystemId));
        if (stationSystemIdError is not null)
        {
            return ToProblem(stationSystemIdError);
        }

        var statuses = await _monitoringService
            .GetTargetStatusesAsync(scopeResult.Value, stationSystemId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeTargetStatusesResponse(
            statuses.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpGet("sessions/{sessionId:guid}/timeline")]
    [ProducesResponseType<RuntimeTimelineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RuntimeTimelineResponse>> GetSessionTimelineAsync(
        Guid sessionId,
        [FromQuery] string? projectId,
        [FromQuery] string? applicationId,
        [FromQuery] string? projectSnapshotId,
        [FromQuery] string? topologyId,
        [FromQuery] string? productionRunId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest();
        }

        var scopeResult = CreateMonitoringScope(
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionRunId);
        if (scopeResult.IsFailure)
        {
            return ToProblem(scopeResult.Error);
        }

        var timeline = await _monitoringService
            .GetSessionTimelineAsync(
                new RuntimeSessionId(sessionId),
                scopeResult.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeTimelineResponse(
            timeline.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpGet("alarms")]
    [ProducesResponseType<RuntimeAlarmsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeAlarmsResponse>> GetAlarmsAsync(
        [FromQuery] string? stationSystemId,
        [FromQuery] bool includeAcknowledged = false,
        CancellationToken cancellationToken = default)
    {
        var alarms = await _monitoringService
            .GetAlarmsAsync(stationSystemId, includeAcknowledged, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeAlarmsResponse(
            alarms.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpPost("alarms/{alarmId:guid}/acknowledgements")]
    [ProducesResponseType<RuntimeAlarmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeAlarmResponse>> AcknowledgeAlarmAsync(
        Guid alarmId,
        AcknowledgeRuntimeAlarmRequest request,
        CancellationToken cancellationToken)
    {
        if (alarmId == Guid.Empty)
        {
            return BadRequest();
        }

        var result = await _monitoringService
            .AcknowledgeAlarmAsync(
                new RuntimeIncidentId(alarmId),
                request.AcknowledgedBy,
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        var response = RuntimeMonitoringResponseMapper.ToResponse(result.Value);
        await _hubContext.Clients.All
            .AlarmAcknowledged(response)
            .ConfigureAwait(false);

        return Ok(response);
    }

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.StartsWith("Validation.", StringComparison.Ordinal)
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status404NotFound;

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }

    private static Result<RuntimeMonitoringScope> CreateMonitoringScope(
        string? projectId,
        string? applicationId,
        string? projectSnapshotId,
        string? topologyId,
        string? productionRunId)
    {
        if (projectId is null
            || applicationId is null
            || projectSnapshotId is null
            || topologyId is null)
        {
            return Result.Failure<RuntimeMonitoringScope>(ApplicationError.Validation(
                "Runtime.MonitoringScopeRequired",
                "ProjectId, ApplicationId, ProjectSnapshotId, and TopologyId are required."));
        }

        try
        {
            ProductionRunId? parsedProductionRunId = null;
            if (productionRunId is not null)
            {
                if (!Guid.TryParseExact(productionRunId, "D", out var parsedValue)
                    || parsedValue == Guid.Empty
                    || !string.Equals(parsedValue.ToString("D"), productionRunId, StringComparison.Ordinal))
                {
                    return Result.Failure<RuntimeMonitoringScope>(ApplicationError.Validation(
                        "Runtime.ProductionRunFilterInvalid",
                        "ProductionRunId must be null or a non-empty canonical Production Run ID."));
                }

                parsedProductionRunId = new ProductionRunId(parsedValue);
            }

            return Result.Success(new RuntimeMonitoringScope(
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                parsedProductionRunId));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<RuntimeMonitoringScope>(ApplicationError.Validation(
                "Runtime.MonitoringScopeInvalid",
                exception.Message));
        }
    }

    private static ApplicationError? ValidateOptionalCanonical(string? value, string fieldName)
    {
        return value is null
            || (!string.IsNullOrWhiteSpace(value)
                && !char.IsWhiteSpace(value[0])
                && !char.IsWhiteSpace(value[^1]))
            ? null
            : ApplicationError.Validation(
                "Runtime.MonitoringFilterInvalid",
                $"{fieldName} must be null or a non-empty canonical string.");
    }
}
