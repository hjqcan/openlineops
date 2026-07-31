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
    [HttpGet("definitions/{id}")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<AlarmDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlarmDefinitionDetails>> GetDefinition(
        string id,
        CancellationToken cancellationToken)
    {
        var details = await appService
            .GetDefinitionAsync(id, cancellationToken)
            .ConfigureAwait(false);

        return details is null
            ? NotFound()
            : Ok(details);
    }

    [HttpPost("definitions")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
    [ProducesResponseType<AlarmDefinitionCommandResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<AlarmDefinitionCommandResult>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlarmDefinitionCommandResult>> RegisterDefinition(
        RegisterAlarmDefinitionApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .RegisterDefinitionAsync(
                new RegisterAlarmDefinitionRequest(
                    request.Id,
                    request.StationId,
                    request.Source,
                    request.Severity,
                    request.Title,
                    request.Description,
                    request.IsLatching,
                    request.RequiresBuzzer,
                    request.MaximumShelfSeconds,
                    request.EscalationDelaySeconds,
                    request.EscalationAction,
                    User.GetRequiredActorId(),
                    request.CommandId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Conflict(result);
        }

        return result.Replayed
            ? Ok(result)
            : CreatedAtAction(
                nameof(GetDefinition),
                new { id = result.Definition!.Id },
                result);
    }

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
        RaiseAlarmApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.StationId,
                User.GetRequiredStationId(),
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        var result = await appService
            .RaiseCommandAsync(
                new RaiseAlarmRequest(
                    request.Id,
                    request.StationId,
                    request.Source,
                    request.SourceId,
                    request.Severity,
                    request.Title,
                    request.Description,
                    request.RaisedAtUtc,
                    request.DefinitionId,
                    request.CommandId,
                    User.GetRequiredActorId()),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Conflict(result);
        }

        return result.Replayed
            ? Ok(result.Alarm)
            : CreatedAtAction(nameof(Get), new { id = result.Alarm!.Id }, result.Alarm);
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
                new AcknowledgeAlarmRequest(
                    User.GetRequiredActorId(),
                    string.IsNullOrWhiteSpace(request.Comment)
                        ? "Acknowledged."
                        : request.Comment,
                    request.CommandId,
                    request.ExpectedVersion),
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
                    request.ClearanceNote,
                    request.CommandId,
                    request.ExpectedVersion),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{id}/shelving")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OperationsApplicationResult>> Shelf(
        string id,
        ShelfAlarmApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .ShelfAsync(
                id,
                new ShelfAlarmRequest(
                    User.GetRequiredActorId(),
                    request.Comment,
                    request.ShelvedUntilUtc,
                    request.CommandId,
                    request.ExpectedVersion),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpPost("{id}/suppression")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<OperationsApplicationResult>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OperationsApplicationResult>> Suppress(
        string id,
        SuppressAlarmApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await appService
            .SuppressAsync(
                id,
                new SuppressAlarmRequest(
                    User.GetRequiredActorId(),
                    request.SuppressionSource,
                    request.Reason,
                    request.SuppressedUntilUtc,
                    request.CommandId,
                    request.ExpectedVersion),
                cancellationToken)
            .ConfigureAwait(false);

        return ToActionResult(result);
    }

    [HttpGet("{id}/facts")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
    [ProducesResponseType<IReadOnlyCollection<AlarmLifecycleFactDetails>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<AlarmLifecycleFactDetails>>> GetFacts(
        string id,
        CancellationToken cancellationToken)
    {
        var facts = await appService
            .GetFactsAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return Ok(facts);
    }

    private ActionResult<OperationsApplicationResult> ToActionResult(
        OperationsApplicationResult result)
    {
        if (result.Succeeded)
        {
            return Ok(result);
        }

        if (string.Equals(result.Code, "Operations.Alarm.NotFound", StringComparison.Ordinal))
        {
            return NotFound(result);
        }

        return result.Code is "Operations.Alarm.IdempotencyConflict"
            or "Operations.Alarm.VersionConflict"
            ? Conflict(result)
            : BadRequest(result);
    }
}
