using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Api.Models;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Results;
using OpenLineOps.Operations.Application.Contract.Services;

namespace OpenLineOps.Operations.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route(OpenLineOpsApiRoutes.OperationsAlarms)]
public sealed class AlarmsController(IAlarmAppService appService)
    : ControllerBase
{
    [HttpGet("{id}")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<AlarmDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlarmDetails>> Get(
        string id,
        CancellationToken cancellationToken)
    {
        var details = await appService.GetAsync(id, cancellationToken).ConfigureAwait(false);

        return details is null
            ? NotFound()
            : Ok(details);
    }

    [HttpGet("open")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<IReadOnlyCollection<AlarmDetails>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<AlarmDetails>>> GetOpenByStation(
        [FromQuery] string stationId,
        CancellationToken cancellationToken)
    {
        var alarms = await appService
            .GetOpenByStationAsync(stationId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(alarms);
    }

    [HttpPost]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<AlarmDetails>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AlarmDetails>> Raise(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.StationId,
                User.GetRequiredStationId(),
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        var details = await appService.RaiseAsync(request, cancellationToken).ConfigureAwait(false);

        return CreatedAtAction(nameof(Get), new { id = details.Id }, details);
    }

    [HttpPost("{id}/acknowledgement")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationsApplicationResult>> Acknowledge(
        string id,
        AcknowledgeAlarmApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .AcknowledgeAsync(
                id,
                new AcknowledgeAlarmRequest(User.GetRequiredActorId()),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{id}/source-clearance")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationsApplicationResult>> ClearSource(
        string id,
        ClearAlarmSourceApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .ClearSourceAsync(
                id,
                new ClearAlarmSourceRequest(
                    User.GetRequiredActorId(),
                    User.GetRequiredStationId(),
                    request.ClearanceNote),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    private ActionResult<OperationsApplicationResult> ToActionResult(
        OperationsApplicationResult result)
    {
        if (result.Succeeded)
        {
            return Ok(result);
        }

        return string.Equals(result.Code, "Operations.Alarm.NotFound", StringComparison.Ordinal)
            ? NotFound(result)
            : BadRequest(result);
    }
}
