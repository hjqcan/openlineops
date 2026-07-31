using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Maintenance.Api.Mapping;
using OpenLineOps.Maintenance.Api.Models;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;

namespace OpenLineOps.Maintenance.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route(MaintenanceApiRoutes.Root)]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class MaintenanceEngineeringController(
    IEquipmentMaintenanceService maintenanceService) : ControllerBase
{
    [HttpPost("assets")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> RegisterAssetAsync(
        RegisterEquipmentAssetRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await maintenanceService.RegisterAsync(
                new RegisterEquipmentAssetCommand(
                    request.AssetId,
                    request.StationId,
                    request.DisplayName,
                    request.ProductionCritical,
                    request.RequiresCalibration,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MaintenanceApiMapper.ToProblem(result.Error);
        }

        var response = MaintenanceApiMapper.ToResponse(result.Value);
        return Created(
            $"{MaintenanceApiRoutes.Root}/assets/{Uri.EscapeDataString(response.AssetId)}",
            response);
    }

    [HttpPost("assets/{assetId}/plans")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> AddPlanAsync(
        string assetId,
        AddMaintenancePlanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request.CalendarIntervalSeconds is <= 0)
        {
            return MaintenanceApiMapper.ToProblem(
                ApplicationError.Validation(
                    "Maintenance.Plan.CalendarInterval",
                    "Calendar interval seconds must be positive when configured."));
        }

        var result = await maintenanceService.AddPlanAsync(
                new AddMaintenancePlanCommand(
                    assetId,
                    request.PlanId,
                    request.DisplayName,
                    request.CycleInterval,
                    request.OperatingHoursInterval,
                    request.CalendarIntervalSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : null,
                    request.BlocksProduction,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    [HttpPost("assets/{assetId}/calibrations")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> RecordCalibrationAsync(
        string assetId,
        RecordCalibrationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryParseDefined(request.Status, out CalibrationStatus status))
        {
            return MaintenanceApiMapper.ToProblem(
                ApplicationError.Validation(
                    "Maintenance.Calibration.Status",
                    "Calibration status must be an exact defined status name."));
        }

        var result = await maintenanceService.RecordCalibrationAsync(
                new RecordCalibrationCommand(
                    assetId,
                    status,
                    request.Reference,
                    request.ValidUntilUtc,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    private static bool TryParseDefined<TEnum>(
        string value,
        out TEnum parsed)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed)
        && Enum.IsDefined(parsed);
}
