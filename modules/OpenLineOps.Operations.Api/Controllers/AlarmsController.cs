using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Results;
using OpenLineOps.Operations.Application.Contract.Services;

namespace OpenLineOps.Operations.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.OperationsV1)]
[Route(OpenLineOpsApiRoutes.OperationsAlarms)]
public sealed class AlarmsController(IAlarmAppService appService)
    : ControllerBase
{
    [HttpGet("{id}")]
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
    [ProducesResponseType<AlarmDetails>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AlarmDetails>> Raise(
        RaiseAlarmRequest request,
        CancellationToken cancellationToken)
    {
        var details = await appService.RaiseAsync(request, cancellationToken).ConfigureAwait(false);

        return CreatedAtAction(nameof(Get), new { id = details.Id }, details);
    }

    [HttpPost("{id}/acknowledgement")]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationsApplicationResult>> Acknowledge(
        string id,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .AcknowledgeAsync(id, request, cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{id}/resolution")]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationsApplicationResult>> Resolve(
        string id,
        ResolveAlarmRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .ResolveAsync(id, request, cancellationToken)
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
