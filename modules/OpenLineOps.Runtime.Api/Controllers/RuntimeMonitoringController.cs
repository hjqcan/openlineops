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
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.RuntimeV1)]
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
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        var statuses = await _monitoringService
            .GetStationStatusesAsync(stationSystemId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeStationStatusesResponse(
            statuses.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpGet("targets")]
    [ProducesResponseType<RuntimeTargetStatusesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeTargetStatusesResponse>> GetTargetStatusesAsync(
        [FromQuery] string? stationSystemId,
        CancellationToken cancellationToken)
    {
        var statuses = await _monitoringService
            .GetTargetStatusesAsync(stationSystemId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RuntimeTargetStatusesResponse(
            statuses.Select(RuntimeMonitoringResponseMapper.ToResponse).ToArray()));
    }

    [HttpGet("sessions/{sessionId:guid}/timeline")]
    [ProducesResponseType<RuntimeTimelineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RuntimeTimelineResponse>> GetSessionTimelineAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return BadRequest();
        }

        var timeline = await _monitoringService
            .GetSessionTimelineAsync(new RuntimeSessionId(sessionId), cancellationToken)
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
}
