using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Api.Mapping;
using OpenLineOps.Maintenance.Api.Models;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;

namespace OpenLineOps.Maintenance.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route(MaintenanceApiRoutes.Root)]
[Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class MaintenanceOperatorController(
    IEquipmentMaintenanceService maintenanceService,
    IClock clock) : ControllerBase
{
    [HttpGet("assets/{assetId}")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquipmentAssetResponse>> GetAssetAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        var result = await maintenanceService.GetAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    [HttpPost("assets/{assetId}/tasks/{taskId}/completion")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> CompleteTaskAsync(
        string assetId,
        string taskId,
        CompleteMaintenanceTaskRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await maintenanceService.CompleteTaskAsync(
                new CompleteMaintenanceTaskCommand(
                    assetId,
                    taskId,
                    request.CompletionNote,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.CompletedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    [HttpGet("stations/{stationId}/production-readiness")]
    [ProducesResponseType<StationProductionReadinessResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StationProductionReadinessResponse>>
        EvaluateProductionReadinessAsync(
            string stationId,
            CancellationToken cancellationToken)
    {
        var result = await maintenanceService.EvaluateProductionStartAsync(
                stationId,
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }
}
